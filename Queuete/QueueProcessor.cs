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
        private readonly object locker = new object();
        private readonly AutoResetEvent waiter = new AutoResetEvent(false);

        internal CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private TaskCompletionSource<object> stopped;
        private TaskCompletionSource<object> idled;
        private ImmutableDictionary<QueueItemType, ItemQueue> queuesByType = ImmutableDictionary<QueueItemType, ItemQueue>.Empty;
        private ImmutableList<ItemQueue> queues = ImmutableList<ItemQueue>.Empty;

        private ItemQueue GetQueue(QueueItemType type)
        {
            lock (locker)
            {
                ItemQueue queue;
                if (!queuesByType.TryGetValue(type, out queue))
                {
                    queue = new ItemQueue(type);
                    queues = queues.Add(queue);
                    queuesByType = queuesByType.Add(type, queue);
                }
                return queue;
            }
        }

        private IEnumerable<ItemQueue> GetAvailableQueues()
        {
            foreach (var queue in queues)
            {
                if (queue.IsAvailable)
                    yield return queue;
            }
        }

        public QueueItem Enqueue(QueueItemType type, Func<QueueItem, Task> action)
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

        public Task Stop()
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

            while (!cancellationToken.IsCancellationRequested)
            {
                var wasItemDequeued = false;
                foreach (var queue in GetAvailableQueues())
                {
                    var queueItem = queue.Dequeue();
                    wasItemDequeued = true;

                    queue.Activate(queueItem);
                    Task.Run(() => queueItem.Execute().ContinueWith(_ =>
                    {
                        queue.Deactivate(queueItem);
                        checkIdle();
                    }));
                }
                if (!wasItemDequeued)
                {
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