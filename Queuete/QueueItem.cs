using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueItem
    {
        public QueueItemType Type { get; }
        public QueueItemState State { get; set; } = QueueItemState.Waiting;
        public Exception Error { get; private set; }

        internal QueueProcessor processor;

        private readonly object locker = new object();
        private readonly Func<Task> action;

        private IImmutableQueue<QueueItem> dependents = ImmutableQueue<QueueItem>.Empty;

        protected QueueItem(QueueItemType type, Func<Task> action)
        {
            Type = type;
            this.action = action;
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after this item has finished.  If this item has already finished,
        /// then it will simply be enqueued on the main processor.
        /// </summary>
        /// <param name="dependent"></param>
        public void EnqueueDependent(QueueItem dependent)
        {
            lock (locker)
            {
                // If we've already finished, then there is no dependency, so just enqueue the item as a normal item.
                if (State == QueueItemState.Finished)
                {
                    processor.Enqueue(dependent);
                }
                else
                {
                    dependent.State = QueueItemState.Blocked;
                    dependents = dependents.Enqueue(dependent);
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
                await action();
            }
            catch (Exception ex)
            {
                Error = ex;
            }

            lock (locker)
            {
                if (Error != null)
                {
                    State = QueueItemState.Errored;
                }
                else
                {
                    State = QueueItemState.Finished;

                    while (dependents.Any())
                    {
                        QueueItem dependent;
                        dependents = dependents.Dequeue(out dependent);
                        processor.Enqueue(dependent);
                    }                                    
                }
            }
        }
    }
}