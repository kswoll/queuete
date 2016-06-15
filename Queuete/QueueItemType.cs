namespace Queuete
{
    public class QueueItemType
    {
        public string Label { get; }
        public int MaxConcurrentItems { get; }

        public QueueItemType(string label, int maxConcurrentItems)
        {
            Label = label;
            MaxConcurrentItems = maxConcurrentItems;
        }
    }
}