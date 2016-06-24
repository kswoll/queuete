using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueItem
    {
        public QueueItemType Type { get; }
        public QueueItemState State { get; set; } = QueueItemState.Waiting;
        public Exception Error { get; private set; }
        public CancellationToken CancellationToken => processor.cancellationToken.Token;

        internal QueueProcessor processor;

        private readonly object locker = new object();
        private readonly Func<QueueItem, Task> action;

        /// <summary>
        /// When this item completes, these dependents will be enqueued in their original order.
        /// </summary>
        private ImmutableQueue<QueueItem> dependents = ImmutableQueue<QueueItem>.Empty;

        /// <summary>
        /// The inverse of dependents.  All the items in this list must finish before this queue item is
        /// free to run.
        /// </summary>
        private ImmutableList<QueueItem> dependencies = ImmutableList<QueueItem>.Empty;

        internal QueueItem(QueueItemType type, Func<QueueItem, Task> action)
        {
            Type = type;
            this.action = action;
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after this item has finished.  If this item has already finished,
        /// then it will simply be enqueued on the main processor.
        /// </summary>
        public QueueItem EnqueueDependent(QueueItemType type, Func<QueueItem, Task> action)
        {
            lock (locker)
            {
                // If we've already finished, then there is no dependency, so just enqueue the item as a normal item.
                if (State == QueueItemState.Finished)
                {
                    return processor.Enqueue(type, action);
                }
                else
                {
                    var dependent = new QueueItem(type, action);
                    dependent.State = QueueItemState.Blocked;
                    dependents = dependents.Enqueue(dependent);
                    return dependent;
                }
            }            
        }

        internal async Task Execute()
        {
            lock (locker)
            {
                State = QueueItemState.Running;
            }

            try
            {
                await action(this);
            }
            catch (Exception ex)
            {
                Error = ex;
            }

            if (Error != null)
            {
                lock (locker)
                {
                    State = QueueItemState.Errored;
                }
            }
            else
            {
                QueueItem[] dependents;
                lock (locker)
                {
                    State = QueueItemState.Finished;
                    dependents = this.dependents.ToArray();
                    this.dependents = ImmutableQueue<QueueItem>.Empty;
                }

                foreach (var dependent in dependents)
                {
                    lock (dependent.locker)
                    {
                        // This item is complete, so any item that has a dependency on it should have that dependency removed
                        dependent.dependencies = dependent.dependencies.Remove(this);

                        // Only process the item if is free of any dependencies
                        if (!dependent.dependencies.Any())
                        {
                            processor.Enqueue(dependent);
                        }
                    }
                }
            }
        }
    }
}