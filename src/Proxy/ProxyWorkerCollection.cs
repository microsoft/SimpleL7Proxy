using Microsoft.ApplicationInsights;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Proxy;

public class ProxyWorkerCollection
{
  private readonly List<ProxyWorker> _workers;
  private readonly List<Task> _tasks;
  private readonly CancellationToken _cancellationToken;

  public ProxyWorkerCollection(
    BackendOptions backendOptions, 
    IBlockingPriorityQueue<RequestData> queue, 
    IBackendService backends,
    IEventClient eventClient,
    TelemetryClient telemetryClient,
    CancellationToken cancellationToken)
  {
    _workers = new List<ProxyWorker>();
    _tasks = new List<Task>();

    for (int i = 0; i < backendOptions.Workers; i++)
    {
      var pw = new ProxyWorker(cancellationToken, i, queue, backendOptions, backends, eventClient, telemetryClient);
      _workers.Add(pw);
      _cancellationToken = cancellationToken; // FIXME
    }

  }

  public void StartWorkers()
  {
    foreach (var pw in _workers)
      _tasks.Add(Task.Run(() => pw.TaskRunner(), _cancellationToken));
  }

  public Task GetAllProxyWorkerTasks()
  {
    return Task.WhenAll(_tasks);
  }

}
