using Azure.Messaging.EventHubs.Producer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Messaging;

namespace SimpleL7Proxy.Events;

public class EventHubClient : IEventClient, IHostedService, IDisposable
{
    private bool _disposed = false;

    private readonly EventHubConfig? _config;
    private readonly IBatchMessageTransport<EventDataBatch>? _transport;
    private readonly BatchMessagePump<EventDataBatch>? _pump;
    private readonly ILogger<EventHubClient> _logger;
    private readonly CompositeEventClient _composite;
    private const string DefaultDestination = "eventhub";

    public static int ReconnectCount = 0;

    public EventHubClient(CompositeEventClient composite, 
        IOptions<ProxyConfig> options, 
        ILogger<EventHubClient> logger,
        DefaultCredential defaultCredential)
    {
        var BackendOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(defaultCredential);

        try {
            _config = new EventHubConfig(BackendOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize EventHubConfig. EventHubClient will be disabled.");
            _config = null;
        }

        _composite = composite ?? throw new ArgumentNullException(nameof(composite));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_config is not null)
        {
            var transport = new EventHubBatchTransport(_config, defaultCredential, _logger);
            _transport = transport;
            _pump = new BatchMessagePump<EventDataBatch>(
                destination: DefaultDestination,
                transport: transport,
                createBatchAsync: cancellationToken => transport.CreateBatchAsync(DefaultDestination, cancellationToken),
                recoverBatchAsync: RecoverBatchAsync,
                options: new BatchMessagePumpOptions
                {
                    FlushCountThreshold = 10,
                    FlushInterval = TimeSpan.FromSeconds(2),
                    WaitThreshold = BackendOptions.MaxUndrainedEvents / 4,
                    ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
                });
        }
    }

    public int Count => _pump?.Count ?? 0;
    public int FlushedLastMinute => _pump?.FlushedLastMinute ?? 0;
    public string ClientType => _pump?.IsRunning == true ? "EventHub" : "EventHub (Disabled)";

    public bool IsHealthy()
    {
        return _pump is not null && _pump.IsRunning && ReconnectCount == 0 && !_pump.IsShuttingDown;
    }

    public void BeginShutdown()
    {
        _pump?.BeginShutdown();
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        if (_config == null)
        {
            _logger.LogInformation("EventHubClient configuration is null. EventHub will not be started.");
            return;
        }

        if (_pump == null)
        {
            _logger.LogInformation("EventHubClient pump is null. EventHub will not be started.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.StartupSeconds));
        try {
            await _pump.StartAsync(cts.Token).ConfigureAwait(false);
            
            _composite.Add(this);
            var ConnString = string.IsNullOrEmpty(_config.ConnectionString) ? "Not Set" : "Set";
            _logger.LogInformation("[EVENTHB] ✓ EventHub Client started: ConnectionString: {ConnString}, Name: {EventHubName}, Namespace: {EventHubNamespace}", ConnString, _config.EventHubName, _config.EventHubNamespace);
        }
        catch (OperationCanceledException) {
            _logger.LogError("EventHubClient setup timed out after {Seconds} seconds. EventHub logging will be disabled.", _config.StartupSeconds);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to setup EventHubClient. EventHub logging will be disabled.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // Shutdown is owned by CompositeEventClient to preserve ordering during coordinated stop.
        return Task.CompletedTask;
    }

    public async Task StopTimerAsync()
    {
        if (_pump == null)
        {
            return;
        }

        await _pump.StopAsync().ConfigureAwait(false);

        if (_pump.Count > 0)
        {
            _logger.LogWarning("[SHUTDOWN] EventHubClient stopped with {Count} items still in queue.", _pump.Count);
        }
    }

    public void SendData(string? value)
    {
        _pump?.Enqueue(value);
    }

    private async ValueTask<EventDataBatch> RecoverBatchAsync(CancellationToken cancellationToken)
    {
        if (_transport == null || _config == null)
        {
            throw new InvalidOperationException("EventHub transport is not initialized.");
        }

        try
        {
            await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        Interlocked.Exchange(ref ReconnectCount, 0);

        for (int attempt = 1; attempt <= _config.MaxReconnectAttempts; attempt++)
        {
            Interlocked.Increment(ref ReconnectCount);
            try
            {
                await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
                var batch = await _transport.CreateBatchAsync(DefaultDestination, cancellationToken).ConfigureAwait(false);

                Interlocked.Exchange(ref ReconnectCount, 0);
                return batch;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "EventHubClient: Reconnect attempt {Attempt} failed.", attempt);
                await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new Exception("EventHubClient: Failed to reconnect after multiple attempts.");
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _pump?.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
