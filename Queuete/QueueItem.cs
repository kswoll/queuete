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

        private readonly QueueAction action;

        private int state = (int)QueueItemState.Pending;
        private int isInitialized;

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

        internal void InitializeItem(QueueProcessor processor, ItemQueue queue)
        {
            if (Interlocked.CompareExchange(ref isInitialized, 1, 0) == 0)
            {
                this.processor = processor;
                this.queue = queue;
            }
        }

        internal void EnqueueDependent(QueueItem dependent)
        {
            processor.Dispatch(_ => dependents = dependents.Enqueue(dependent));
        }

        internal void AddDependency(QueueItem dependency)
        {
            processor.Dispatch(_ => dependencies = dependencies.Add(dependency));
        }

        public QueueItemState State
        {
            get
            {
                return (QueueItemState)state;
            }
            set
            {
                var intValue = (int)value;
                var changed = Interlocked.CompareExchange(ref state, intValue, state) != intValue;
                var stateChanged = StateChanged;
                if (changed && stateChanged != null)
                {
                    // Important, we never want to run this on the current thread.  In particular, if the current thread
                    // is the processor thread, all hell can break loose in terms of timing.
                    Task.Run(() => StateChanged?.Invoke(this, value));
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
                TaskCompletionSource<object> completionSource = new TaskCompletionSource<object>();
                processor.Dispatch(_ =>
                {
                    QueueItem[] dependents = this.dependents.ToArray();
                    this.dependents = ImmutableQueue<QueueItem>.Empty;

                    foreach (var dependent in dependents)
                    {
                        // This item is complete, so any item that has a dependency on it should have that dependency removed
                        dependent.dependencies = dependent.dependencies.Remove(this);

                        // Only process the item if it is free of any dependencies
                        if (!dependent.dependencies.Any())
                        {
                            processor.Enqueue(dependent);
                        }
                    }
                    completionSource.SetResult(null);
                });
                await completionSource.Task;
            }
        }
    }
}