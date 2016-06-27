using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueItem
    {
        public event Action<QueueItem, QueueItemState> StateChanged;

        public QueueItemType Type { get; }
        public Exception Error { get; private set; }
        public CancellationToken CancellationToken => queue.cancellationToken.Token;

        internal QueueProcessor processor;
        internal ItemQueue queue;

        private readonly object locker = new object();
        private readonly QueueAction action;

        private QueueItemState state = QueueItemState.Pending;

        /// <summary>
        /// When this item completes, these dependents will be enqueued in their original order.
        /// </summary>
        private ImmutableQueue<QueueItem> dependents = ImmutableQueue<QueueItem>.Empty;

        /// <summary>
        /// The inverse of dependents.  All the items in this list must finish before this queue item is
        /// free to run.
        /// </summary>
        private ImmutableList<QueueItem> dependencies = ImmutableList<QueueItem>.Empty;

        public QueueItem(QueueItemType type, QueueAction action)
        {
            this.action = action;

            Type = type;
        }

        public QueueItemState State
        {
            get { return state; }
            set
            {
                if (state != value)
                {
                    state = value;
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after this item has finished.  If this item has already finished,
        /// then it will simply be enqueued on the main processor.
        /// </summary>
        public QueueItem EnqueueDependent(QueueItemType type, QueueAction action)
        {
            var dependent = new QueueItem(type, action);
            EnqueueDependent(dependent);
            return dependent;
        }

        public void EnqueueDependent(QueueItem dependent)
        {
            var queue = processor.GetQueue(dependent.Type);
            queue.InitializeItem(dependent);

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
                State = QueueItemState.Finished;
                QueueItem[] dependents;
                lock (locker)
                {
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