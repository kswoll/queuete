using System;

namespace Queuete
{
    public class QueueItemType
    {
        public string Label { get; }
        public int MaxConcurrentItems { get; }

        public QueueItemType(string label, int maxConcurrentItems)
        {
            if (maxConcurrentItems <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentItems), "Value must be greater than zero.");

            Label = label;
            MaxConcurrentItems = maxConcurrentItems;
        }

        public override string ToString()
        {
            return Label;
        }
    }
}