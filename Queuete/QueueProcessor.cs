using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueProcessor
    {
        private static readonly QueueStopReason defaultStopReason = new QueueStopReason("Default", (processor, itemType) => itemType.IsCancellable(processor));

        private readonly object locker = new object();
        private readonly AutoResetEvent waiter = new AutoResetEvent(false);

        private TaskCompletionSource<object> stopped;
        private TaskCompletionSource<object> idled;
        private ImmutableDictionary<QueueItemType, ItemQueue> queuesByType = ImmutableDictionary<QueueItemType, ItemQueue>.Empty;
        private ImmutableList<ItemQueue> queues = ImmutableList<ItemQueue>.Empty;
        private QueueStopReason stopReason;

        public bool IsQueueIdle(QueueItemType type)
        {
            return GetQueue(type).IsIdle;
        }

        private ItemQueue GetQueue(QueueItemType type)
        {
            lock (locker)
            {
                ItemQueue queue;
                if (!queuesByType.TryGetValue(type, out queue))
                {
                    queue = new ItemQueue(this, type);
                    queues = queues.Add(queue);
                    queuesByType = queuesByType.Add(type, queue);
                }
                return queue;
            }
        }

        public QueueItem Enqueue(QueueItemType type, QueueAction action)
        {
            var item = new QueueItem(type, action);
            Enqueue(item);
            return item;
        }

        public void Enqueue(QueueItem item)
        {
            var queue = GetQueue(item.Type);
            queue.InitializeItem(item);
            queue.Enqueue(item);
            waiter.Set();
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after this item has finished.  If this item has already finished,
        /// then it will simply be enqueued on the main processor.
        /// </summary>
        public QueueItem EnqueueDependent(IEnumerable<QueueItem> dependencies, QueueItemType type, QueueAction action)
        {
            var dependent = new QueueItem(type, action);
            EnqueueDependent(dependencies, dependent);
            return dependent;
        }

        public void EnqueueDependent(IEnumerable<QueueItem> dependencies, QueueItem dependent)
        {
            var queue = GetQueue(dependent.Type);
            queue.InitializeItem(dependent);

            bool areDependenciesFinished = dependencies.All(x => x.State == QueueItemState.Finished);

            // If we've already finished, then there is no dependency, so just enqueue the item as a normal item.
            if (areDependenciesFinished)
            {
                Enqueue(dependent);
            }
            else
            {
                dependent.State = QueueItemState.Blocked;
                foreach (var dependency in dependencies)
                {
                    if (dependency.State != QueueItemState.Finished)
                    {
                        dependency.EnqueueDependent(dependent);
                        dependent.AddDependency(dependency);
                    }
                }
            }
        }

        public void Start()
        {
            idled = new TaskCompletionSource<object>();
            Task.Run(() => Process());
        }

        public void Stop(QueueStopReason reason = null)
        {
            var waiter = new ManualResetEvent(false);
            var stopAsync = StopAsync(reason);
            stopAsync.ContinueWith(_ => waiter.Set());
            waiter.WaitOne();
        }

        public Task StopAsync(QueueStopReason reason = null)
        {
            stopReason = reason = reason ?? defaultStopReason;
            stopped = new TaskCompletionSource<object>();

            lock (locker)
            {
                foreach (var queue in queues.Where(x => reason.IsCancellable(this, x.Type)))
                {
                    queue.Cancel();
                }
            }

            waiter.Set();
            return stopped.Task;
        }

        public Task WaitForIdle()
        {
            return idled.Task;
        }

        private void Process()
        {
            // Called at any point where we might have become idle:
            // * When no queue provided an item to process on a given round
            // * When a queue item finishes processing it might be the last one
            Action checkIdle = () =>
            {
                lock (locker)
                {
                    if (queues.All(x => x.IsIdle))
                    {
                        idled.SetResult(null);
                        idled = new TaskCompletionSource<object>();
                    }
                }
            };

            // Each iteration here we'll call a round
            while (true)
            {
                var wasItemDequeued = false;
                foreach (var queue in queues)
                {
                    if (queue.IsAvailable && (!queue.cancellationToken.IsCancellationRequested || stopReason.IsCancellable(this, queue.Type)))
                    {
                        QueueItem queueItem;
                        lock (locker)
                        {
                            queueItem = queue.Dequeue();
                            wasItemDequeued = true;
                            queue.Activate(queueItem);
                        }
                        Task.Run(() => queueItem.Execute().ContinueWith(_ =>
                        {
                            queue.Deactivate(queueItem);
                            waiter.Set();
                            checkIdle();
                        }));
                    }
                    else
                    {
                        queue.MarkPending();
                    }
                }
                if (!wasItemDequeued)
                {
                    if (stopReason != null)
                        break;

                    checkIdle();

                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    waiter.WaitOne();
                }
            }
            Debug.Assert(stopped != null);

            stopReason = null;
            stopped.SetResult(null);
        }
    }
}