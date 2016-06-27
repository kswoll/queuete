using System;

namespace Queuete
{
    public class QueueItemType
    {
        public string Label { get; }
        public int MaxConcurrentItems { get; }

        private readonly Func<QueueProcessor, QueueItemType, bool> isCancellable;
        private readonly Func<QueueProcessor, QueueItemType, bool> isBlocked;

        public QueueItemType(string label, int maxConcurrentItems, Func<QueueProcessor, QueueItemType, bool> isCancellable = null, Func<QueueProcessor, QueueItemType, bool> isBlocked = null)
        {
            if (maxConcurrentItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentItems), "Value must be greater than zero.");

            this.isCancellable = isCancellable ?? ((processor, item) => true);
            this.isBlocked = isBlocked ?? ((processor, item) => false);

            Label = label;
            MaxConcurrentItems = maxConcurrentItems;
        }

        public bool IsCancellable(QueueProcessor processor)
        {
            return isCancellable(processor, this);
        }

        public bool IsBlocked(QueueProcessor processor)
        {
            return isBlocked(processor, this);
        }

        public override string ToString()
        {
            return Label;
        }
    }
}