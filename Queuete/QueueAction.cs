using System.Threading.Tasks;

namespace Queuete
{
    public delegate Task QueueAction(QueueItem item);
}