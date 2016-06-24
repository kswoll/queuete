using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    internal class ItemQueue
    {
        public int Count => count;
        public bool IsIdle => count == 0;
        public bool IsAvailable => count < type.MaxConcurrentItems && count > 0;

        private readonly QueueItemType type;
        private readonly object locker = new object();

        private ImmutableQueue<QueueItem> queue = ImmutableQueue<QueueItem>.Empty;
        private ImmutableHashSet<QueueItem> activeItems = ImmutableHashSet<QueueItem>.Empty;
        private int count;

        public ItemQueue(QueueItemType type)
        {
            this.type = type;
        }

        public void Enqueue(QueueItem item)
        {
            Interlocked.Increment(ref count);
            lock (locker)
            {
                queue = queue.Enqueue(item);
            }
        }

        public QueueItem Dequeue()
        {
            Interlocked.Decrement(ref count);
            lock (locker)
            {
                QueueItem item;
                queue = queue.Dequeue(out item);
                return item;
            }
        }

        public async Task Execute(QueueItem item)
        {
            lock (locker)
            {
                activeItems = activeItems.Add(item);
            }

            await item.Execute();

            lock (locker)
            {
                activeItems = activeItems.Add(item);
            }
        }
    }
}