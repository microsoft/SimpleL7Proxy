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
  private readonly IConcurrentPriQueue<RequestData> _queue;
  private readonly IBackendService _backends;
  private readonly IEventClient _eventClient;
  private readonly TelemetryClient _telemetryClient;
  private readonly ILogger<ProxyWorker> _logger;
  private readonly ProxyStreamWriter _proxyStreamWriter;

  private readonly List<ProxyWorker> _workers;
  private readonly List<Task> _tasks;

  public ProxyWorkerCollection(
    IOptions<BackendOptions> backendOptions, 
    IConcurrentPriQueue<RequestData> queue, 
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

    var workerPriorities = new Dictionary<int, int>(backendOptions.PriorityWorkers);
    Console.WriteLine($"Worker Priorities: {string.Join(",", workerPriorities)}");

    // The loop creates a number of workers based on backendOptions.Workers.
    // The first worker (wrkrNum == 0) is always a probe worker with priority 0.
    // Subsequent workers are assigned priorities based on the available counts in workerPriorities.
    // If no specific priority is available, the worker is assigned a fallback priority (Constants.AnyPriority).
    for (int wrkrNum = 0; wrkrNum <= backendOptions.Workers; wrkrNum++)
    {
      int workerPriority;

      // Determine the priority for this worker
      if (wrkrNum == 0)
      {
          workerPriority = 0; // Probe worker
      }
      else
      {
          workerPriority = workerPriorities.FirstOrDefault(kvp => kvp.Value > 0).Key;
          if (workerPriority != 0)
          {
              workerPriorities[workerPriority]--;
          }
          else
          {
              workerPriority = Constants.AnyPriority;
          }
      }

      _workers.Add(new(
        wrkrNum,
        workerPriority,
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
