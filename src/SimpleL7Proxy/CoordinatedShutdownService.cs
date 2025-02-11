using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;

namespace SimpleL7Proxy;

public class CoordinatedShutdownService : IHostedService
{
    private readonly IHostApplicationLifetime _appLifetime;
    // Inject other services if needed
    private readonly ILogger<CoordinatedShutdownService> _logger;
    private readonly Server _server;
    private readonly BackendOptions _options;
    private readonly IEventClient? _eventClient;
    private readonly IConcurrentPriQueue<RequestData> _queue;
    private readonly IBackendService _backends;


    public CoordinatedShutdownService(IHostApplicationLifetime appLifetime,
        IOptions<BackendOptions> backendOptions,
        IConcurrentPriQueue<RequestData> queue,
        IBackendService backends,
        IEventClient? eventClient,
        ILogger<CoordinatedShutdownService> logger,
        Server server)
    {
        _appLifetime = appLifetime;
        _logger = logger;
        _server = server;
        _queue = queue;
        _backends = backends;
        _eventClient = eventClient;
        _options = backendOptions.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Coordinated shutdown initiated...");
        await _server.StopAsync(cancellationToken);
        _logger.LogInformation($"Waiting for tasks to complete for maximum {_options.TerminationGracePeriodSeconds} seconds");

        _eventClient?.SendData($"Server shutting down:   {ProxyWorker.GetState()}");

        await _queue.StopAsync();

        var timeoutTask = Task.Delay(_options.TerminationGracePeriodSeconds * 1000);


        var allTasksComplete = Task.WhenAll(ProxyWorkerCollection.GetAllTasks());
        var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask);
        if (completedTask == timeoutTask)
        {
            _logger.LogInformation($"Tasks did not complete within {_options.TerminationGracePeriodSeconds} seconds. Forcing shutdown.");
        }
        else
        {
            _logger.LogInformation("All tasks completed.");
        }

        Task? t = _backends?.Stop();
        if (t != null)
            await t.ConfigureAwait(false); // Stop the backend pollers


        _eventClient?.SendData($"Workers Stopped:   {ProxyWorker.GetState()}");
        _eventClient?.StopTimer();
        //await Task.CompletedTask;
    }

}