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
        private static readonly QueueItemType secondItemType = new QueueItemType("second", 1);

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

            var item2 = new QueueItem(testItemType, async _ => didItem2Run = true);
            processor.Enqueue(item2);

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

            // This last line isn't strictly necessary for the purposes of this test, but seems cleaner if we follow through.
            Assert.IsTrue(didItem2Run);
        }

        [Test]
        public async Task DifferentTaskTypesRunConcurrently()
        {
            var processor = new QueueProcessor();
            processor.Start();

            // Start up the first task that won't complete until we're sure both tasks started
            var item1CompletionSource = new TaskCompletionSource<object>();
            var waiter1 = new ManualResetEvent(false);
            var didTask1Finish = false;
            processor.Enqueue(testItemType, async _ =>
            {
                waiter1.Set();
                await item1CompletionSource.Task;
                didTask1Finish = true;
            });

            // Start up the second task that won't complete until we're sure both tasks started
            var item2CompletionSource = new TaskCompletionSource<object>();
            var waiter2 = new ManualResetEvent(false);
            var didTask2Finish = false;
            processor.Enqueue(secondItemType, async _ =>
            {
                waiter2.Set();
                await item2CompletionSource.Task;
                didTask2Finish = true;
            });

            WaitHandle.WaitAll(new[] { waiter1, waiter2 });
            item1CompletionSource.SetResult(null);
            item2CompletionSource.SetResult(null);

            // Wait for everything to finish.
            await processor.WaitForIdle();

            // These last two line aren't strictly necessary for the purposes of this test, but seems cleaner if we follow through.
            Assert.IsTrue(didTask1Finish);
            Assert.IsTrue(didTask2Finish);
        }

        [Test]
        public async Task DependentTaskWaitsInLine()
        {
            var processor = new QueueProcessor();
            processor.Start();

            // Start up the first task that won't complete until we're sure both tasks started
            var item1CompletionSource = new TaskCompletionSource<object>();
            var item1 = processor.Enqueue(testItemType, async _ =>
            {
                await item1CompletionSource.Task;
            });

            // Start up the second task that won't complete until the first one is done
            var didTask2Finish = false;
            var item2 = new QueueItem(secondItemType, async _ =>
            {
                didTask2Finish = true;
            });

            item2.StateChanged += (item, state) =>
            {
                // Now that we know the desired state has been reached, we can allow the first task to continue
                if (state == QueueItemState.Blocked)
                    item1CompletionSource.SetResult(null);
            };

            item1.EnqueueDependent(item2);

            // Wait for everything to finish.
            await processor.WaitForIdle();

            // These last two line aren't strictly necessary for the purposes of this test, but seems cleaner if we follow through.
            Assert.IsTrue(didTask2Finish);
        }

        [Test]
        public async Task NonCancellableTaskCompletesOnCancel()
        {
            var processor = new QueueProcessor();
            processor.Start();

            // Start up the first task that won't complete until we're sure both tasks started
            var item1CompletionSource = new TaskCompletionSource<object>();
            var waiter = new ManualResetEvent(false);
            var didItem1Finish = false;
            processor.Enqueue(new QueueItemType("noncancellable", 1), async _ =>
            {
                waiter.Set();
                await item1CompletionSource.Task;
                didItem1Finish = true;
            });

            waiter.WaitOne();

            var stop = processor.StopAsync();

            item1CompletionSource.SetResult(null);

            await stop;

            Assert.IsTrue(didItem1Finish);
        }

        [Test]
        public async Task CustomBlockingObeyed()
        {
            var processor = new QueueProcessor();
            processor.Start();

            // Start up the first task that won't complete until we're sure both tasks started
            var item1CompletionSource = new TaskCompletionSource<object>();
            processor.Enqueue(testItemType, async _ =>
            {
                await item1CompletionSource.Task;
            });

            var didItem2Finish = false;
            var blockedType = new QueueItemType("blocked", 1, isBlocked: (_, type) => !processor.IsQueueIdle(testItemType));
            var item2 = new QueueItem(blockedType, async _ => didItem2Finish = true);
            item2.StateChanged += (item, state) =>
            {
                if (state == QueueItemState.Waiting)
                    item1CompletionSource.SetResult(null);
            };
            processor.Enqueue(item2);

            // Wait for everything to finish.
            await processor.WaitForIdle();

            Assert.IsTrue(didItem2Finish);
        }
    }
}