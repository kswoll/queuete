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
            var queueItem = new QueueItem(testItemType, async _ => executed = true);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
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
            Assert.AreEqual(QueueItemState.Waiting, queueItem.State);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await processor.WaitForIdle();
            Assert.AreEqual(QueueItemState.Errored, queueItem.State);
            Assert.AreEqual("foo", queueItem.Error.Message);
        }
    }
}