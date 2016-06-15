using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Queuete
{
    public class QueueProcessor
    {
        private readonly object locker = new object();
        private readonly AutoResetEvent waiter = new AutoResetEvent(false);

        private TaskCompletionSource<object> stopped;
        private ImmutableQueue<QueueItem> queue = ImmutableQueue<QueueItem>.Empty;
        private CancellationTokenSource cancellationToken = new CancellationTokenSource();

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
            Task.Run(Process);
        }

        public Task Stop()
        {
            stopped = new TaskCompletionSource<object>();
            stopped.Task.ContinueWith(_ => cancellationToken = new CancellationTokenSource());

            cancellationToken.Cancel();
            return stopped.Task;
        }

        private async Task Process()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                QueueItem queueItem;
                lock (locker)
                {
                    queueItem = queue.Peek();
                    if (queueItem != null)
                    {
                        queue = queue.Dequeue();
                    }
                }

                if (queueItem != null)
                {
                    await queueItem.Execute();
                }
                else
                {                    
                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    WaitHandle.WaitAny(new[] { waiter, cancellationToken.Token.WaitHandle });
                }
            }
            Debug.Assert(stopped != null);

            stopped.SetResult(null);
        }
    }
}