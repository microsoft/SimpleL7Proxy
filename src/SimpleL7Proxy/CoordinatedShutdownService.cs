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
    private readonly ProxyConfig _options;
    private readonly IEventClient? _eventClient;
    private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly IBackupAPIService _backupAPIService;
    private readonly IConcurrentPriQueue<RequestData> _queue;
    private readonly IBackendService _backends;
    private readonly IAsyncFeeder _asyncFeeder;
    private readonly IRequeueWorker _requeueWorker;
    private readonly BlobWriteQueue? _blobWriteQueue;
    private readonly BlobWriter? _blobWriter;
    private readonly IEnumerable<IShutdownParticipant> _shutdownParticipants;
    private readonly ProbeServer _probeServer;
    private readonly CompositeEventClient _compositeEventClient;


    public CoordinatedShutdownService(IHostApplicationLifetime appLifetime,
        IOptions<ProxyConfig> backendOptions,
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
        _blobWriter = serviceProvider.GetService<BlobWriter>();
        _shutdownParticipants = serviceProvider.GetServices<IShutdownParticipant>();
        _probeServer = probeServer;
        _options = backendOptions.Value;
        _compositeEventClient = serviceProvider.GetRequiredService<CompositeEventClient>();
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
        await _server.StopListening(cancellationToken).ConfigureAwait(false);
        var timeoutTask = Task.Delay(_options.TerminationGracePeriodSeconds * 1000, CancellationToken.None);
        _logger.LogInformation($"[SHUTDOWN] ⏳============ Begin Shutdown - Maximum {_options.TerminationGracePeriodSeconds}s");
        _compositeEventClient?.BeginShutdown(); // signal EventHubClient to begin agressively flushing

        // Stop AsyncFeeder and server first to prevent it from generating new work
        await _asyncFeeder.StopAsync(cancellationToken).ConfigureAwait(false);
        Task requeueTask = _requeueWorker.CancelAllCancelableTasks(); // cancel all cancellable delays
        
        var allTasksComplete = Task.WhenAll(WorkerFactory.GetAllTasks());
        WorkerFactory.ExpelAsyncRequests();  // backup all async requests
        WorkerFactory.RequestWorkerShutdown();

        // Logger task to periodically log the shutdown status
        Task watcherTask = new Task(async () =>
        {
            int counter=0;
            while ( ! allTasksComplete.IsCompleted )
            {
                int queueCount = _queue?.thrdSafeCount ?? 0;
                if ( counter++ % 5 == 0) // Log every 5 iterations to avoid spamming
                    Console.WriteLine($"[SHUTDOWN] Queue: {queueCount}, Workers: { HealthCheckService.GetWorkerState() }");
                if (queueCount == 0 )
                {
                    await _queue!.StopAsync().ConfigureAwait(false);
                    // no more work to do
                    break;
                }
                await Task.Delay(200).ConfigureAwait(false);
            }
        });
        watcherTask.Start();

        await requeueTask.ConfigureAwait(false); // wait for all cancellable delays to be cancelled

        var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask).ConfigureAwait(false);
        if (completedTask == timeoutTask)
        {
            _logger.LogInformation("[SHUTDOWN] ⚠ Grace period expired ({GracePeriod}s) — queue: {Queue}, workers: {States}",
                _options.TerminationGracePeriodSeconds, _queue.thrdSafeCount, HealthCheckService.GetWorkerState());
        }
        else
        {
            _logger.LogInformation("[SHUTDOWN] ✓ All tasks completed");
        }
        await _queue!.StopAsync().ConfigureAwait(false);

        ProxyEvent data=new() 
        {
            ["EventType"] = "S7P-Shutdown",
            ["Message"] = "Coordinated shutdown in process.",
            ["Timestamp"] = DateTime.UtcNow.ToString("o"),
            ["BackendStatus"] = _backends.HostStatus,
            ["QueueCount"] = _queue.thrdSafeCount.ToString(),
            ["WorkerStates"] = HealthCheckService.GetWorkerState()
        };
        data.SendEvent();

        Task? t = _backends.Stop();
        if (t != null)
            await t.ConfigureAwait(false); // Stop the backend pollers

        // Discover and invoke all IShutdownParticipant implementations, ordered by ShutdownOrder.
        // Same pattern as IHostedService — register as IShutdownParticipant in DI, get discovered here.
        foreach (var participant in _shutdownParticipants.OrderBy(p => p.ShutdownOrder))
        {
            _logger.LogInformation("[SHUTDOWN] ⏹ Shutting down {Service} (order {Order})",
                participant.GetType().Name, participant.ShutdownOrder);
            await participant.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        
        if (_backendTokenProvider != null)
            await _backendTokenProvider.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // // BackupAPIService is NOT registered as IHostedService - we control its shutdown explicitly here
        // // to ensure it stops AFTER all proxy workers have completed and flushed their status updates
        // if (_backupAPIService != null)
        //     await _backupAPIService.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // ServiceBusRequestService is stopped explicitly here for ordering control
        if (_serviceBusRequestService != null)
            await _serviceBusRequestService.StopAsync(cancellationToken).ConfigureAwait(false);
        
        // BlobWriteQueue is stopped LAST before probes - all producers (proxy workers, async workers, backup service)
        // are guaranteed to be done at this point, so no more enqueues will happen
        if (_blobWriteQueue != null)
        {
            _logger.LogInformation("[SHUTDOWN] ⏹ Stopping BlobWriteQueue (final flush)");
            await _blobWriteQueue.StopAsync(CancellationToken.None).ConfigureAwait(false);
            // Dispose underlying BlobWriter after the queue has flushed
            _blobWriter?.Dispose();
        }

        // Health probes are stopped at the VERY END so the container orchestrator
        // (e.g. Kubernetes, Container Apps) continues to see healthy probes while
        // other services drain. If probes fail early, the orchestrator may kill the pod.
        Console.WriteLine("[SHUTDOWN] ⏹ Stopping health probes");
        await _probeServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _server.StopProbes(CancellationToken.None).ConfigureAwait(false);
        await _compositeEventClient!.StopTimerAsync().ConfigureAwait(false);
        Console.WriteLine("[SHUTDOWN] ✅ Service Stopped");
    }

}