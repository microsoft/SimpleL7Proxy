using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Async.BackupAPI;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy;

/// <summary>
/// One-shot hosted service that wires up <see cref="RequestData"/> static references
/// for async-mode processing.  Runs during the hosted-service startup phase so that
/// all dependencies are ready before Server and WorkerFactory begin accepting traffic.
/// </summary>
public sealed class AsyncInitializer : IHostedService
{
    private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly IBackupAPIService _backupAPIService;
    private readonly IUserPriorityService _userPriorityService;
    private readonly ProxyConfig _options;
    private readonly ILogger<AsyncInitializer> _logger;

    public AsyncInitializer(
        IServiceBusRequestService serviceBusRequestService,
        IBackupAPIService backupAPIService,
        IUserPriorityService userPriorityService,
        IOptions<ProxyConfig> options,
        ILogger<AsyncInitializer> logger)
    {
        _serviceBusRequestService = serviceBusRequestService;
        _backupAPIService = backupAPIService;
        _userPriorityService = userPriorityService;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        RequestData.InitializeServiceBusRequestService(
            _serviceBusRequestService,
            _backupAPIService,
            _userPriorityService,
            _options);

        _logger.LogInformation("[STARTUP] ✓ RequestData async statics initialized");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
