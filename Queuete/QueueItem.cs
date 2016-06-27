using System;
using System.Collections.Generic;
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
        public IEnumerable<QueueItem> Dependents => dependents;

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

        internal void EnqueueDependent(QueueItem dependent)
        {
            lock (locker)
            {
                dependents = dependents.Enqueue(dependent);
            }
        }

        internal void AddDependency(QueueItem dependency)
        {
            lock (locker)
            {
                dependencies = dependencies.Add(dependency);
            }
        }

        public QueueItemState State
        {
            get
            {
                return state;
            }
            set
            {
                var intState = (int)state;
                var intValue = (int)value;
                var changed = Interlocked.CompareExchange(ref intState, intValue, intValue) != intValue;
                if (changed)
                {
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        internal async Task Execute()
        {
            State = QueueItemState.Running;

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
                State = QueueItemState.Errored;
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