using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Queue;

public class PriorityQueueItem<T>
{
  public T Item { get; }
  public int Priority { get; }
  public DateTime Timestamp { get; }

  public PriorityQueueItem(T item, int priority, DateTime timestamp)
  {
    Item = item;
    Priority = priority;
    Timestamp = timestamp;
  }
}
