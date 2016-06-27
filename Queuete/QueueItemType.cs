using System;

namespace Queuete
{
    public class QueueItemType
    {
        public string Label { get; }
        public int MaxConcurrentItems { get; }
        public bool IsCancellable { get; }

        private Func<QueueProcessor, QueueItemType, bool> isBlocked;

        public QueueItemType(string label, int maxConcurrentItems, bool isCancellable = true, Func<QueueProcessor, QueueItemType, bool> isBlocked = null)
        {
            if (maxConcurrentItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentItems), "Value must be greater than zero.");

            this.isBlocked = isBlocked ?? ((processor, item) => false);

            Label = label;
            MaxConcurrentItems = maxConcurrentItems;
            IsCancellable = isCancellable;
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