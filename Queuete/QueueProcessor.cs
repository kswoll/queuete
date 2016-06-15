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
        private ImmutableQueue<QueueItem> queue = ImmutableQueue<QueueItem>.Empty;

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
            Task.Run(Process);
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

        private async Task Process()
        {
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
                    await queueItem.Execute();
                }
                else
                {
                    lock (locker)
                    {
                        idled.SetResult(null);
                        idled = new TaskCompletionSource<object>();
                    }

                    // Wait for either a new item to be enqueued (waiter) or for the cancellation token to be triggered
                    WaitHandle.WaitAny(new[] { waiter, cancellationToken.Token.WaitHandle });
                }
            }
            Debug.Assert(stopped != null);

            stopped.SetResult(null);
        }
    }
}