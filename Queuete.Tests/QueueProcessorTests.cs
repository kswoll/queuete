using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

#pragma warning disable 1998

namespace Queuete.Tests
{
    [TestFixture]
    public class QueueProcessorTests
    {
        private static readonly QueueItemType testItemType = new QueueItemType("test", 1);

        [Test]
        public async Task RunOneItem()
        {
            var executed = false;

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(testItemType, async _ => executed = true);
            await processor.WaitForIdle();

            Assert.IsTrue(executed);
        }

        [Test]
        public async Task ItemState()
        {
            var queueItem = new QueueItem(testItemType, async x =>
            {
                Assert.AreEqual(QueueItemState.Running, x.State);
            });
            Assert.AreEqual(QueueItemState.Pending, queueItem.State);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await processor.WaitForIdle();
            Assert.AreEqual(QueueItemState.Finished, queueItem.State);
        }

        [Test]
        public async Task ErrorState()
        {
            var queueItem = new QueueItem(testItemType, async x =>
            {
                throw new Exception("foo");
            });
            Assert.AreEqual(QueueItemState.Pending, queueItem.State);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await processor.WaitForIdle();
            Assert.AreEqual(QueueItemState.Errored, queueItem.State);
            Assert.AreEqual("foo", queueItem.Error.Message);
        }

        [Test]
        public async Task ErrorStateAsync()
        {
            var queueItem = new QueueItem(testItemType, async x =>
            {
                await Task.Delay(0);
                throw new Exception("foo");
            });
            Assert.AreEqual(QueueItemState.Pending, queueItem.State);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await processor.WaitForIdle();
            Assert.AreEqual(QueueItemState.Errored, queueItem.State);
            Assert.AreEqual("foo", queueItem.Error.Message);
        }

        [Test]
        public async Task Stop()
        {
            // We're going to set up a task to set this to true.  We're also going to have that task
            // wait forever before setting it to true.  So, it's really not going to get set to true,
            // (though we set it to true anyway to make things more clear)  So really, the failed state
            // of this test will be if it hangs.  We're trying to stop the processor, which should cancel
            // the task.  
            var executed = false;
            var queueItem = new QueueItem(testItemType, async x =>
            {
                await Task.Delay(int.MaxValue, x.CancellationToken);
                executed = true;
            });

            var processor = new QueueProcessor();
            processor.Start();

            // Set up a waiter to block the main test until after the code in the StateChanged event below
            // has executed.
            var waiter = new ManualResetEvent(false);
            queueItem.StateChanged += (item, state) =>
            {
                // If we just started the task, go ahead and force the queue processor to stop.  It will 
                // cancel the task, and, since we used the cancellation token, it will abort immediately.
                if (state == QueueItemState.Running)
                    processor.Stop();

                // Now release the waiter we set up above
                waiter.Set();
            };

            // Enqueue that task and wait for the above state changed logic to finish.
            processor.Enqueue(queueItem);
            waiter.WaitOne();

            // We've gotten here, which means all the active tasks have been cancelleed, and we can assert
            // what certainly must be true.  We could omit this, but it's probably clearer to leave around.
            Assert.IsFalse(executed);
        }

        [Test]
        public async Task MaxConcurrentCountObeyed()
        {
            var processor = new QueueProcessor();
            processor.Start();

            // Start up the first task that won't complete until we're sure the second task has been forced to wait
            var item1CompletionSource = new TaskCompletionSource<object>();
            processor.Enqueue(testItemType, _ => item1CompletionSource.Task);

            // When the second task sets this we know the chain of events has happened in order
            bool didItem2Run = false;

            var item2 = processor.Enqueue(testItemType, async _ => didItem2Run = true);

            // When the second item has had its state changed to waiting, it means that we had reached our max concurrent 
            // count and been forced into a waiting state.  Bingo!  That's the main point of this test.
            item2.StateChanged += (item, state) =>
            {
                // Now that we know the desired state has been reached, we can allow the first task to continue
                if (state == QueueItemState.Waiting)
                    item1CompletionSource.SetResult(null);
            };

            // Wait for everything to finish
            await processor.WaitForIdle();

            // Wait for task 2 to complete.  These last two line aren't strictly necessary for the purposes of this test,
            // but seems cleaner if we follow through.
            Assert.IsTrue(didItem2Run);
        }
    }
}