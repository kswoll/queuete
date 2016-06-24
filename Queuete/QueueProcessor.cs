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
        private readonly Dictionary<QueueItemType, Counter> countOfConcurrentTasksByItemType = new Dictionary<QueueItemType, Counter>();

        internal CancellationTokenSource cancellationToken = new CancellationTokenSource();

        private TaskCompletionSource<object> stopped;
        private TaskCompletionSource<object> idled;
        private ImmutableQueue<QueueItem> queue = ImmutableQueue<QueueItem>.Empty;

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
                queue = queue.Enqueue(item);
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
                if (countOfConcurrentTasksByItemType.All(x => x.Value.Count == 0))
                {
                    idled.SetResult(null);
                    idled = new TaskCompletionSource<object>();
                }
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                QueueItem queueItem = null;
                lock (locker)
                {
                    if (queue.Any())
                    {
                        queue = queue.Dequeue(out queueItem);
                    }
                }

                if (queueItem != null)
                {
                    lock (locker)
                    {
                        Counter counter;
                        if (!countOfConcurrentTasksByItemType.TryGetValue(queueItem.Type, out counter))
                        {
                            counter = new Counter();
                            countOfConcurrentTasksByItemType[queueItem.Type] = counter;
                        }
                        countOfConcurrentTasksByItemType[queueItem.Type].Count++;
                    }
                    Task.Run(() => queueItem.Execute().ContinueWith(_ =>
                    {
                        lock (locker)
                        {
                            countOfConcurrentTasksByItemType[queueItem.Type].Count--;
                            checkIdle();
                        }
                    }));
                }
                else
                {
                    lock (locker)
                    {
                        checkIdle();
                    }

                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    WaitHandle.WaitAny(new[] { waiter, cancellationToken.Token.WaitHandle });
                }
            }
            Debug.Assert(stopped != null);

            stopped.SetResult(null);
        }

        private class Counter
        {
            public int Count { get; set; }
        }
    }
}