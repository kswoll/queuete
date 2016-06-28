using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    /// <summary>
    /// Top-level class that manages the execution of individual queue items.
    ///
    /// Note: Most actions that mutate state internally are not synchronized.  The implementation depends on all
    /// actions that mutate internal state to operate on the processor thread.  This greatly simplifies the logic
    /// vs. trying to synchronize on atomic units of work.
    /// </summary>
    public class QueueProcessor
    {
        public Action<string> Logger { get; set; }

        private static readonly QueueStopReason defaultStopReason = new QueueStopReason("Default", (processor, itemType) => itemType.IsCancellable(processor));

        private readonly object queueCreationLocker = new object();
        private readonly AutoResetEvent waiter = new AutoResetEvent(false);
        private readonly ConcurrentQueue<QueueDispatch> dispatches = new ConcurrentQueue<QueueDispatch>();

        private TaskCompletionSource<object> stopped;
        private TaskCompletionSource<object> idled;
        private TaskCompletionSource<object> waiting;
        private ImmutableDictionary<QueueItemType, ItemQueue> queuesByType = ImmutableDictionary<QueueItemType, ItemQueue>.Empty;
        private ImmutableList<ItemQueue> queues = ImmutableList<ItemQueue>.Empty;
        private QueueStopReason stopReason;
        private Thread processorThread;

        public void Log(string message)
        {
            Logger?.Invoke(message);
        }

        public bool IsQueueIdle(QueueItemType type)
        {
            return GetQueue(type).IsIdle;
        }

        private ItemQueue GetQueue(QueueItemType type)
        {
            lock (queueCreationLocker)
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

        internal void EnsureOnProcessorThread([CallerFilePath]string callerFile = null, [CallerMemberName]string callerMember = null)
        {
            if (Thread.CurrentThread != processorThread)
                throw new Exception($"Can only invoke {callerMember} in file {callerFile} on the processor thread.");
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
            Dispatch(_ => GetQueue(item.Type).Enqueue(item));
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after the specified dependency has finished.  If this
        /// dependency has already finished, then it will simply be enqueued on the main processor.
        /// </summary>
        public QueueItem EnqueueDependent(QueueItem dependency, QueueItemType type, QueueAction action)
        {
            return EnqueueDependent(new[] { dependency }, type, action);
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after the specified dependency has finished.  If this
        /// dependency has already finished, then it will simply be enqueued on the main processor.
        /// </summary>
        public void EnqueueDependent(QueueItem dependency, QueueItem dependent)
        {
            EnqueueDependent(new[] { dependency }, dependent);
        }

        /// <summary>
        /// Enqueues the specified dependent to only execute after the specified dependencies have finished.  If all the
        /// dependencies have already finished, then it will simply be enqueued on the main processor.
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

            Dispatch(_ =>
            {
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
            });
        }

        public void Start()
        {
            idled = new TaskCompletionSource<object>();
            waiting = new TaskCompletionSource<object>();
            processorThread = new Thread(Process);
            processorThread.Start();
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

            Dispatch(_ =>
            {
                foreach (var queue in queues.Where(x => reason.IsCancellable(this, x.Type)))
                {
                    queue.Cancel();
                }
            });

            return stopped.Task;
        }

        public Task WaitForIdle()
        {
            Log("Waiting for idle");
            return idled.Task;
        }

        public Task WaitForWaiting()
        {
            Log("Waiting for processor to be waiting");
            return waiting.Task;
        }

        /// <summary>
        /// Executes the specified action on the queue thread
        /// </summary>
        public void Dispatch(QueueDispatch action, [CallerMemberName]string callerMemberName = null)
        {
            if (Thread.CurrentThread == processorThread)
            {
                Log($"Dispatch enqueued from {callerMemberName} when already on the processor thread, running inline.");
                action(this);
            }
            else
            {
                dispatches.Enqueue(action);
                Log($"Dispatch enqueued from {callerMemberName}, kicking waiter");
                waiter.Set();
            }
        }

        private void ProcessDispatches()
        {
            QueueDispatch dispatch;
            while (dispatches.TryDequeue(out dispatch))
                dispatch(this);
        }

        private void Process()
        {
            // Each iteration here we'll call a round
            while (true)
            {
                // Process any requests that have come in that want to perform blocking work on the processor thread.
                ProcessDispatches();

                var wasItemDequeued = false;
                foreach (var queue in queues)
                {
                    if (queue.IsAvailable && (!queue.cancellationToken.IsCancellationRequested || stopReason.IsCancellable(this, queue.Type)))
                    {
                        var queueItem = queue.Dequeue();
                        wasItemDequeued = true;
                        queue.Activate(queueItem);

                        Task.Run(() => queueItem.Execute().ContinueWith(_ => Dispatch(processor => queue.Deactivate(queueItem))));
                    }
                    else
                    {
                        queue.MarkWaiting();
                    }
                }
                if (!wasItemDequeued && !dispatches.Any())
                {
                    if (stopReason != null)
                        break;

                    if (queues.All(x => x.IsIdle))
                    {
                        Log("All queues idle, triggering idle task");
                        idled.SetResult(null);
                        idled = new TaskCompletionSource<object>();
                    }

                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    Log("Round finished, waiting");
                    waiting.SetResult(null);
                    waiting = new TaskCompletionSource<object>();
                    waiter.WaitOne();
                }
            }
            Debug.Assert(stopped != null);

            stopReason = null;
            stopped.SetResult(null);
        }
    }
}