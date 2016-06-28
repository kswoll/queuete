using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Queuete
{
    internal class ItemQueue
    {
        public QueueItemType Type => type;
        public int Count => count;
        public bool IsIdle => count == 0 && activeCount == 0;
        public bool IsAvailable => activeCount < type.MaxConcurrentItems && count > 0 && !type.IsBlocked(processor) && !activeItems.Any(x => x.Dependents.Any());

        private readonly QueueProcessor processor;
        private readonly QueueItemType type;

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
            processor.EnsureOnProcessorThread();

            count++;
            queue = queue.Enqueue(item);
        }

        public QueueItem Dequeue()
        {
            processor.EnsureOnProcessorThread();

            count--;
            QueueItem item;
            queue = queue.Dequeue(out item);
            return item;
        }

        public void Activate(QueueItem item)
        {
            processor.EnsureOnProcessorThread();

            activeCount++;
            activeItems = activeItems.Add(item);
        }

        public void Deactivate(QueueItem item)
        {
            processor.EnsureOnProcessorThread();

            activeCount--;
            activeItems = activeItems.Remove(item);
        }

        public void MarkWaiting()
        {
            foreach (var item in queue)
                item.State = QueueItemState.Waiting;
        }

        public void NotifyStopped()
        {
            cancellationToken = new CancellationTokenSource();
        }

        public void InitializeItem(QueueItem item)
        {
            item.InitializeItem(processor, this);
        }

        public override string ToString()
        {
            return type.Label;
        }
    }
}