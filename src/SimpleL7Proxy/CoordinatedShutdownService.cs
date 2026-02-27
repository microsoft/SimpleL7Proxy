using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.BackupAPI;
using SimpleL7Proxy.Feeder;

namespace SimpleL7Proxy;

public class CoordinatedShutdownService : IHostedService
{
    private readonly IHostApplicationLifetime _appLifetime;
    // Inject other services if needed
    private readonly ILogger<CoordinatedShutdownService> _logger;
    private readonly Server _server;
    private readonly BackendTokenProvider _backendTokenProvider;
    private readonly BackendOptions _options;
    private readonly IEventClient? _eventClient;
    private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly IBackupAPIService _backupAPIService;
    private readonly IConcurrentPriQueue<RequestData> _queue;
    private readonly IBackendService _backends;
    private readonly IAsyncFeeder _asyncFeeder;
    private readonly IRequeueWorker _requeueWorker;
    private readonly BlobWriteQueue? _blobWriteQueue;
    private readonly ProbeServer _probeServer;


    public CoordinatedShutdownService(IHostApplicationLifetime appLifetime,
        IOptions<BackendOptions> backendOptions,
        IConcurrentPriQueue<RequestData> queue,
        BackendTokenProvider backendTokenProvider,
        IBackendService backends,
        IEventClient? eventClient,
        IServiceBusRequestService serviceBusRequestService,
        IAsyncFeeder asyncFeeder,
        IBackupAPIService backupAPIService,
        IRequeueWorker requeueWorker,
        IServiceProvider serviceProvider,
        ProbeServer probeServer,
        ILogger<CoordinatedShutdownService> logger,
        Server server)
    {
        _appLifetime = appLifetime;
        _logger = logger;
        _server = server;
        _queue = queue;
        _backends = backends;
        _eventClient = eventClient;
        _serviceBusRequestService = serviceBusRequestService;
        _backupAPIService = backupAPIService;
        _asyncFeeder = asyncFeeder;
        _backendTokenProvider = backendTokenProvider;
        _requeueWorker = requeueWorker;
        _blobWriteQueue = serviceProvider.GetService<BlobWriteQueue>();
        _probeServer = probeServer;
        _options = backendOptions.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Start services explicitly since they're no longer registered as IHostedService
        // (we control their shutdown ordering in StopAsync)
        if (_blobWriteQueue != null)
            await _blobWriteQueue.StartAsync(cancellationToken).ConfigureAwait(false);
        await _probeServer.StartAsync(cancellationToken).ConfigureAwait(false);
        
        if (_serviceBusRequestService is IHostedService sbHosted)
            await sbHosted.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SHUTDOWN] ⏹ Coordinated shutdown initiated");
        _logger.LogInformation($"[SHUTDOWN] ⏳ Waiting for tasks to complete - Maximum {_options.TerminationGracePeriodSeconds}s");

        // Stop AsyncFeeder and server first to prevent it from generating new work
        await _server.StopListening(cancellationToken).ConfigureAwait(false);
        await _asyncFeeder.StopAsync(cancellationToken).ConfigureAwait(false);
        await _queue.StopAsync().ConfigureAwait(false);

        ProxyWorkerCollection.ExpelAsyncRequests();  // backup all async requests
        Task requeueTask = _requeueWorker.CancelAllCancelableTasks(); // cancel all cancellable delays 
        ProxyWorkerCollection.RequestWorkerShutdown();


        var timeoutTask = Task.Delay(_options.TerminationGracePeriodSeconds * 1000, CancellationToken.None);
        await requeueTask.ConfigureAwait(false); // wait for all cancellable delays to be cancelled

        var allTasksComplete = Task.WhenAll(ProxyWorkerCollection.GetAllTasks());
        var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask).ConfigureAwait(false);
        if (completedTask == timeoutTask)
        {
            _logger.LogInformation($"Tasks did not complete within {_options.TerminationGracePeriodSeconds} seconds. Forcing shutdown.");
        }
        else
        {
            _logger.LogInformation("[SHUTDOWN] ✓ All tasks completed");
        }

        ProxyEvent data=new() 
        {
            ["EventType"] = "S7P-Shutdown",
            ["Message"] = "Coordinated shutdown completed.",
            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
            ["BackendStatus"] = _backends.HostStatus,
            ["QueueCount"] = _queue.thrdSafeCount.ToString(),
            ["ActiveWorkers"] = HealthCheckService.ActiveWorkers.ToString(),
            ["WorkerStates"] = string.Join(", ", HealthCheckService.GetWorkerState())
        };
        data.SendEvent();


        Task? t = _backends.Stop();
        if (t != null)
            await t.ConfigureAwait(false); // Stop the backend pollers

        data = new()
        {
            ["EventType"] = "S7P-Shutdown",
            ["Message"] = "Backend pollers stopped.",
            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
            ["BackendStatus"] = _backends.HostStatus,
            ["QueueCount"] = _queue.thrdSafeCount.ToString(),
            ["ActiveWorkers"] = HealthCheckService.ActiveWorkers.ToString(),
            ["WorkerStates"] = string.Join(", ", HealthCheckService.GetWorkerState())
        };
        data.SendEvent();
        
        if (_backendTokenProvider != null)
            await _backendTokenProvider.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // BackupAPIService is NOT registered as IHostedService - we control its shutdown explicitly here
        // to ensure it stops AFTER all proxy workers have completed and flushed their status updates
        if (_backupAPIService != null)
            await _backupAPIService.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // ServiceBusRequestService is stopped explicitly here for ordering control
        if (_serviceBusRequestService != null)
            await _serviceBusRequestService.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // BlobWriteQueue is stopped LAST before probes - all producers (proxy workers, async workers, backup service)
        // are guaranteed to be done at this point, so no more enqueues will happen
        if (_blobWriteQueue != null)
        {
            _logger.LogInformation("[SHUTDOWN] ⏹ Stopping BlobWriteQueue (final flush)");
            await _blobWriteQueue.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        
        if (_eventClient != null)
            await _eventClient.StopTimerAsync().ConfigureAwait(false);

        // Health probes are stopped at the VERY END so the container orchestrator
        // (e.g. Kubernetes, Container Apps) continues to see healthy probes while
        // other services drain. If probes fail early, the orchestrator may kill the pod.
        _logger.LogInformation("[SHUTDOWN] ⏹ Stopping health probes");
        await _probeServer.StopAsync().ConfigureAwait(false);
        await _server.StopProbes(CancellationToken.None).ConfigureAwait(false);
    }

}