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
            var queueItem = new QueueItem(testItemType, async () => executed = true);

            var processor = new QueueProcessor();
            processor.Start();
            processor.Enqueue(queueItem);
            await processor.WaitForIdle();

            Assert.IsTrue(executed);
        }
    }
}