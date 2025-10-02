using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend;
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
    private readonly BackendOptions _options;
    private readonly IEventClient? _eventClient;
    private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly IBackupAPIService _backupAPIService;
    private readonly IConcurrentPriQueue<RequestData> _queue;
    private readonly IBackendService _backends;
    private readonly IAsyncFeeder _asyncFeeder;


    public CoordinatedShutdownService(IHostApplicationLifetime appLifetime,
        IOptions<BackendOptions> backendOptions,
        IConcurrentPriQueue<RequestData> queue,
        IBackendService backends,
        IEventClient? eventClient,
        IServiceBusRequestService serviceBusRequestService,
        IAsyncFeeder asyncFeeder,
        IBackupAPIService backupAPIService,
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
        _options = backendOptions.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[SHUTDOWN] ⏹ Coordinated shutdown initiated");
        _logger.LogInformation($"[SHUTDOWN] ⏳ Waiting for tasks to complete - Maximum {_options.TerminationGracePeriodSeconds}s");

        // Stop AsyncFeeder and server first to prevent it from generating new work
        await _server.StopListening(cancellationToken);
        await _asyncFeeder.StopAsync(cancellationToken);
        await _queue.StopAsync();

        ProxyWorkerCollection.ExpelAsyncRequests();  // backup all async requests
        ProxyWorkerCollection.RequestWorkerShutdown();

        
        var timeoutTask = Task.Delay(_options.TerminationGracePeriodSeconds * 1000);
        var allTasksComplete = Task.WhenAll(ProxyWorkerCollection.GetAllTasks());
        var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask);
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
            ["ActiveWorkers"] = ProxyWorker.activeWorkers.ToString(),
            ["WorkerStates"] = string.Join(", ", ProxyWorker.GetState())
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
            ["ActiveWorkers"] = ProxyWorker.activeWorkers.ToString(),
            ["WorkerStates"] = string.Join(", ", ProxyWorker.GetState())
        };
        data.SendEvent();

        _backupAPIService?.StopAsync(cancellationToken).ConfigureAwait(false);
        _serviceBusRequestService?.StopAsync(cancellationToken).ConfigureAwait(false);
        _eventClient?.StopTimer();
        //await Task.CompletedTask;
    }

}