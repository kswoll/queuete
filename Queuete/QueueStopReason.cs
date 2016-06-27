using System;

namespace Queuete
{
    public class QueueStopReason
    {
        public string Name { get; }

        private readonly Func<QueueProcessor, QueueItemType, bool> isCancellable;

        public QueueStopReason(string name, Func<QueueProcessor, QueueItemType, bool> isCancellable)
        {
            this.isCancellable = isCancellable;
            Name = name;
        }

        public bool IsCancellable(QueueProcessor processor, QueueItemType itemType)
        {
            return isCancellable(processor, itemType);
        }
    }
}