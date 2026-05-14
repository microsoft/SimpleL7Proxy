using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Abstractions;
using SimpleL7Proxy.Config;
using System.Net;
using System.Threading;

namespace SimpleL7Proxy.Proxy;

public class WorkerFactory : BackgroundService, IReadinessParticipant
{
  public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.Workers;
  public ReadinessRegistry Readiness { get; }

  private readonly ProxyConfig _backendOptions;
  private readonly WorkerContext _context;
  private readonly ILogger<ProxyWorker> _logger;
  //private readonly ProxyStreamWriter _proxyStreamWriter;
  private static readonly List<ProxyWorker> _workers = new();
  private static readonly List<Task> _tasks = new();

  private static readonly CancellationTokenSource _internalCancellationTokenSource = new();

  public WorkerFactory(
    WorkerContext context,
    ReadinessRegistry readiness)
  {
    _context = context;
    _backendOptions = context.BackendOptions;
    _logger = context.Logger;
    Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
  }

  protected override async Task ExecuteAsync(CancellationToken cancellationToken)
  {

    var workerPriorities = new Dictionary<int, int>(_backendOptions.PriorityWorkers);

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

      _workers.Add(new(wrkrNum, workerPriority, _context, _internalCancellationTokenSource.Token));
    }

    _logger.LogInformation($"[WORKER] ✓ Total: {_workers.Count} | Priority distribution: {string.Join(",", workerPriorities)}");
    foreach (var pw in _workers)
      _tasks.Add(Task.Run(() => pw.TaskRunnerAsync(), cancellationToken));

    // Close the Workers readiness gate once enough TaskRunners have entered their loops.
    // Matches the threshold ProxyWorker.TaskRunnerAsync uses to flip s_readyToWork.
    _ = Task.Run(async () =>
    {
      while (HealthCheckService.ActiveWorkers < _backendOptions.Workers)
      {
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
      }
      this.RegisterReady();
    }, cancellationToken);

    await _shutdownSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    
    // Wait for all workers to complete with periodic logging
    var allTasksCompletion = Task.WhenAll(_tasks);
    var logInterval = TimeSpan.FromSeconds(.5);
    
    while (!allTasksCompletion.IsCompleted)
    {
      var completedTask = await Task.WhenAny(allTasksCompletion, Task.Delay(logInterval, cancellationToken))
        .ConfigureAwait(false);
      
      if (completedTask != allTasksCompletion)
      {
        _logger.LogInformation("[WORKER] ⏳ Waiting for {count} workers to complete...", _tasks.Count(t => !t.IsCompleted));
      }
    }
    
    await allTasksCompletion.ConfigureAwait(false);
    
    _logger.LogInformation("[WORKER] ✓ All {count} workers have completed.", _tasks.Count);
    return;
  }

  public static void ExpelAsyncRequests()
  {
    foreach (var worker in _workers)
    {
      worker.ExpelAsyncRequest();
    }
  }

  private static readonly SemaphoreSlim _shutdownSemaphore = new(0,1);
  public static void RequestWorkerShutdown()
  {
    _internalCancellationTokenSource.Cancel();
    _shutdownSemaphore.Release();

  }

  public static List<Task> GetAllTasks()
  {
    return _tasks;
  }
}
