using System.Collections.Immutable;
using System.Threading;

namespace Queuete
{
    internal class ItemQueue
    {
        public QueueItemType Type => type;
        public int Count => count;
        public bool IsIdle => count == 0 && activeCount == 0;
        public bool IsAvailable => activeCount < type.MaxConcurrentItems && count > 0 && !type.IsBlocked(processor);

        private readonly QueueProcessor processor;
        private readonly QueueItemType type;
        private readonly object locker = new object();

        internal CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private ImmutableQueue<QueueItem> queue = ImmutableQueue<QueueItem>.Empty;
        private ImmutableHashSet<QueueItem> activeItems = ImmutableHashSet<QueueItem>.Empty;
        private int count;
        private int activeCount;

        public ItemQueue(QueueProcessor processor, QueueItemType type)
        {
            this.processor = processor;
            this.type = type;
        }

        public void Cancel()
        {
            cancellationToken.Cancel();
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

        public void Activate(QueueItem item)
        {
            Interlocked.Increment(ref activeCount);
            lock (locker)
            {
                activeItems = activeItems.Add(item);
            }
        }

        public void Deactivate(QueueItem item)
        {
            Interlocked.Decrement(ref activeCount);
            lock (locker)
            {
                activeItems = activeItems.Remove(item);
            }
        }

        public void MarkPending()
        {
            foreach (var item in queue)
                item.State = QueueItemState.Waiting;
        }

        public void NotifyStopped()
        {
            cancellationToken = new CancellationTokenSource();
        }
    }
}