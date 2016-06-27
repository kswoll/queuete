using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueProcessor
    {
        private readonly object locker = new object();
        private readonly AutoResetEvent waiter = new AutoResetEvent(false);

        internal CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private TaskCompletionSource<object> stopped;
        private TaskCompletionSource<object> idled;
        private ImmutableDictionary<QueueItemType, ItemQueue> queuesByType = ImmutableDictionary<QueueItemType, ItemQueue>.Empty;
        private ImmutableList<ItemQueue> queues = ImmutableList<ItemQueue>.Empty;

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
            item.processor = this;
            lock (locker)
            {
                GetQueue(item.Type).Enqueue(item);
            }
            waiter.Set();
        }

        public void Start()
        {
            idled = new TaskCompletionSource<object>();
            Task.Run(() => Process());
        }

        public void Stop()
        {
            var waiter = new ManualResetEvent(false);
            var stopAsync = StopAsync();
            stopAsync.ContinueWith(_ => waiter.Set());
            waiter.WaitOne();
        }

        public Task StopAsync()
        {
            stopped = new TaskCompletionSource<object>();
            stopped.Task.ContinueWith(_ => cancellationToken = new CancellationTokenSource());

            cancellationToken.Cancel();
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
                    if (queue.IsAvailable && (!queue.Type.IsCancellable(this) || !cancellationToken.IsCancellationRequested))
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
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    checkIdle();

                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    WaitHandle.WaitAny(new[] { waiter, cancellationToken.Token.WaitHandle });
                }
            }
            Debug.Assert(stopped != null);

            stopped.SetResult(null);
        }
    }
}