using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Abstractions;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using System.Threading;

namespace SimpleL7Proxy.Proxy;

public class ProxyWorkerCollection : BackgroundService
{
  private readonly BackendOptions _backendOptions;
  private readonly IBlockingPriorityQueue<RequestData> _queue;
  private readonly IBackendService _backends;
  private readonly IEventClient _eventClient;
  private readonly TelemetryClient _telemetryClient;
  private readonly ILogger<ProxyWorker> _logger;
  private readonly ProxyStreamWriter _proxyStreamWriter;

  private readonly List<ProxyWorker> _workers;
  private readonly List<Task> _tasks;

  public ProxyWorkerCollection(
    IOptions<BackendOptions> backendOptions, 
    IBlockingPriorityQueue<RequestData> queue, 
    IBackendService backends,
    IEventClient eventClient,
    TelemetryClient telemetryClient,
    ILogger<ProxyWorker> logger,
    ProxyStreamWriter proxyStreamWriter)
  {
    _backendOptions = backendOptions.Value;
    _queue = queue;
    _backends = backends;
    _eventClient = eventClient;
    _telemetryClient = telemetryClient;
    _logger = logger;
    _proxyStreamWriter = proxyStreamWriter;

    _workers = [];
    _tasks = [];    
  }

  protected override Task ExecuteAsync(CancellationToken cancellationToken)
  {
    for (int i = 0; i < _backendOptions.Workers; i++)
    {
      _workers.Add(new(
        i,
        _queue,
        _backendOptions,
        _backends,
        _eventClient,
        _telemetryClient,
        _logger,
        _proxyStreamWriter,
        cancellationToken));
    }

    foreach (var pw in _workers)
      _tasks.Add(Task.Run(() => pw.TaskRunner(), cancellationToken));

    return Task.WhenAll(_tasks);
  }
}
