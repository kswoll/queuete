using System;
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
            Assert.AreEqual(QueueItemState.Waiting, queueItem.State);

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
            var executed = false;
            var queueItem = new QueueItem(testItemType, async x =>
            {
                await Task.Delay(int.MaxValue, x.CancellationToken);
                executed = true;
            });

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await Task.Delay(10);
            await processor.Stop();

            Assert.IsFalse(executed);
        }

        [Test]
        public async Task MaxConcurrentCountObeyed()
        {
            var processor = new QueueProcessor();
            processor.Start();

            var item1CompletionSource = new TaskCompletionSource<object>();
            processor.Enqueue(testItemType, _ => item1CompletionSource.Task);

            bool didItem2Run = false;
            var item2 = processor.Enqueue(testItemType, async _ => didItem2Run = true);
            item2.StateChanged += (item, state) =>
            {
                if (state == QueueItemState.Waiting)
                    item1CompletionSource.SetResult(null);
            };

            await processor.WaitForIdle();

            Assert.IsTrue(didItem2Run);
        }
    }
}