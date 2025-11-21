using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Abstractions;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.StreamProcessor;
using SimpleL7Proxy.User;
using System.Net;
using System.Threading;

namespace SimpleL7Proxy.Proxy;

public class ProxyWorkerCollection : BackgroundService
{
  private readonly BackendOptions _backendOptions;
  private readonly IConcurrentPriQueue<RequestData> _queue;
  private readonly IBackendService _backends;
  private readonly IUserPriorityService _userPriorityService;
  private readonly IUserProfileService _userProfileService;
  private readonly IRequeueWorker _requeueWorker;
  private readonly IEventClient _eventClient;
  private readonly ILogger<ProxyWorker> _logger;
  //private readonly ProxyStreamWriter _proxyStreamWriter;
  private readonly IAsyncWorkerFactory _asyncWorkerFactory;
  private readonly StreamProcessorFactory _streamProcessorFactory;
  private static readonly List<ProxyWorker> _workers = new();
  private static readonly List<Task> _tasks = new();

  private static readonly CancellationTokenSource _internalCancellationTokenSource = new();

  public ProxyWorkerCollection(
    IOptions<BackendOptions> backendOptions,
    IConcurrentPriQueue<RequestData> queue,
    IBackendService backends,
    IUserPriorityService userPriorityService,
    IUserProfileService userProfileService,
    IRequeueWorker requeueWorker,
    IEventClient eventClient,
    ILogger<ProxyWorker> logger,
    IAsyncWorkerFactory asyncWorkerFactory,
    StreamProcessorFactory streamProcessorFactory)
  //,ProxyStreamWriter proxyStreamWriter)
  {
    _backendOptions = backendOptions.Value;
    _queue = queue;
    _backends = backends;
    _eventClient = eventClient;
    _logger = logger;
    //_proxyStreamWriter = proxyStreamWriter;
    _userPriorityService = userPriorityService;
    _userProfileService = userProfileService;
    _asyncWorkerFactory = asyncWorkerFactory;
    _requeueWorker = requeueWorker;
    _streamProcessorFactory = streamProcessorFactory;
  }

  protected override Task ExecuteAsync(CancellationToken cancellationToken)
  {

    var workerPriorities = new Dictionary<int, int>(_backendOptions.PriorityWorkers);
    _logger.LogInformation($"[CONFIG] Worker priority distribution: {string.Join(",", workerPriorities)}");

    // The loop creates a number of workers based on backendOptions.Workers.
    // The first worker (wrkrNum == 0) is always a probe worker with priority 0.
    // Subsequent workers are assigned priorities based on the available counts in workerPriorities.
    // If no specific priority is available, the worker is assigned a fallback priority (Constants.AnyPriority).
    for (int wrkrNum = 0; wrkrNum <= _backendOptions.Workers; wrkrNum++)
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
        _userProfileService,
        _userPriorityService,
        _requeueWorker,
        _eventClient,
        _asyncWorkerFactory,
        _logger,
        _streamProcessorFactory,
        //_proxyStreamWriter,
        _internalCancellationTokenSource.Token));
    }

    foreach (var pw in _workers)
      _tasks.Add(Task.Run(() => pw.TaskRunnerAsync(), cancellationToken));

    return Task.WhenAll(_tasks);
  }

  public static void ExpelAsyncRequests()
  {
    foreach (var worker in _workers)
    {
      worker.ExpelAsyncRequest();
    }
  }

  public static void RequestWorkerShutdown()
  {
    _internalCancellationTokenSource.Cancel();
  }

  public static List<Task> GetAllTasks()
  {
    return _tasks;
  }
}
