using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;

namespace SimpleL7Proxy.Proxy;

public class ProxyWorkerCollection
{
  private readonly List<ProxyWorker> _workers;
  private readonly List<Task> _tasks;
  private readonly CancellationToken _cancellationToken;

  public ProxyWorkerCollection(
    BackendOptions backendOptions, 
    IBlockingPriorityQueue<RequestData> queue, 
    Backends backends,
    IEventClient eventClient,
    TelemetryClient telemetryClient,
    ILogger<ProxyWorker> logger,
    CancellationToken cancellationToken,
    ProxyStreamWriter proxyStreamWriter)
  {
    _workers = [];
    _tasks = [];
    for (int i = 0; i < backendOptions.Workers; i++)
    {
      var pw = new ProxyWorker(cancellationToken, i, queue, backendOptions, backends, eventClient, telemetryClient, logger, proxyStreamWriter);
      _workers.Add(pw);
      _cancellationToken = cancellationToken; // FIXME
    }
  }

  public void StartWorkers()
  {
    foreach (var pw in _workers)
      _tasks.Add(Task.Run(() => pw.TaskRunner(), _cancellationToken));
  }

    public Task GetAllProxyWorkerTasks() => Task.WhenAll(_tasks);
}
