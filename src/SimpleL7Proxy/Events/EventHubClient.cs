using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class EventHubClient : IEventClient, IHostedService, IDisposable
{
    private bool _disposed = false;

    private readonly EventHubConfig? _config;
    private readonly DefaultCredential _defaultCredential;
    private EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private readonly ILogger<EventHubClient> _logger;
    private readonly CompositeEventClient _composite;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private CancellationToken workerCancelToken;
    private volatile bool isRunning = false;
    private volatile bool isShuttingDown = false;
    private volatile bool beginShutdown = false;
    private Task? writerTask;
    private readonly ConcurrentQueue<string> _logBuffer = new();
    private List<string> _pendingItems = new List<string>();
    // Connection parameters retained for reconnection

    private static int entryCount = 0;
    public static int ReconnectCount = 0;

    //public EventHubClient(string connectionString, string eventHubName, ILogger<EventHubClient>? logger = null)

    public EventHubClient(CompositeEventClient composite, 
        IOptions<ProxyConfig> options, 
        ILogger<EventHubClient> logger,
        DefaultCredential defaultCredential)
    {
        var BackendOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _defaultCredential = defaultCredential ?? throw new ArgumentNullException(nameof(defaultCredential));

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
        // All initialization happens in StartAsync
    }

    public int Count => _logBuffer.Count;
    public string ClientType => isRunning ? "EventHub" : "EventHub (Disabled)";

    public bool IsHealthy()
    {
        return isRunning && ReconnectCount == 0 && !isShuttingDown;
    }

    public void BeginShutdown()
    {
        beginShutdown = true;
    }

    public async Task StartAsync(CancellationToken cancellationToken) {
        // If config failed to initialize (constructor threw), skip startup gracefully
        if (_config == null)
        {
            _logger.LogInformation("EventHubClient configuration is null. EventHub will not be started.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.StartupSeconds));
        try {
            
            await ReconnectAsync(cancellationToken: cts.Token).ConfigureAwait(false);
            isRunning = true;
            workerCancelToken = cancellationTokenSource.Token;
            
            _composite.Add(this);
            _logger.LogCritical("[SERVICE] ✓ EventHub Client started successfully");
            writerTask = Task.Run(() => EventWriter(workerCancelToken), workerCancelToken);
        }
        catch (OperationCanceledException) {
            _logger.LogError("EventHubClient setup timed out after {Seconds} seconds. EventHub logging will be disabled.", _config.StartupSeconds);
            // Don't throw — other event clients (e.g. LogFileEventClient) should continue running
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to setup EventHubClient. EventHub logging will be disabled.");
            // Don't throw — other event clients (e.g. LogFileEventClient) should continue running
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopTimerAsync().ConfigureAwait(false);
    }

    public async Task StopTimerAsync()
    {
        isShuttingDown = true;
        var drainDeadline = DateTime.UtcNow.AddSeconds(30);
        while (isRunning && _logBuffer.Count > 0 && DateTime.UtcNow < drainDeadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (_logBuffer.Count > 0)
            _logger.LogWarning("[SHUTDOWN] EventHubClient stopped with {Count} items still in queue.", _logBuffer.Count);

        cancellationTokenSource.Cancel();
        isRunning = false;
        if (writerTask != null)
            await writerTask.ConfigureAwait(false);
    }

    public async Task EventWriter(CancellationToken token)
    {
        if (_batchData is null || _producerClient is null)
        {
            isRunning = false;
            return;
        }

        try
        {
            _logger.LogInformation("EventHubClient: EventWriter running.");
            while (!token.IsCancellationRequested)
            {
                if (GetNextBatch(99) > 0)
                {
                    try
                    {
                        await _producerClient.SendAsync(_batchData, token).ConfigureAwait(false);
                        _pendingItems.Clear();
                        _batchData.Dispose();
                        _batchData = await _producerClient.CreateBatchAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "EventHubClient: SendAsync failed, reconnecting.");
                        // Re-enqueue items from the failed batch so they are not lost
                        foreach (var item in _pendingItems)
                        {
                            _logBuffer.Enqueue(item);
                            Interlocked.Increment(ref entryCount);
                        }
                        _pendingItems.Clear();
                        await ReconnectAsync(cancellationToken: token).ConfigureAwait(false);
                        continue;
                    }
                }

                if (!beginShutdown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
            _logger.LogInformation("[SHUTDOWN] ✓ EventHubClient exiting");
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via cancellation token
        }
        finally
        {
            isRunning = false;

            while (true)
            {
                if (GetNextBatch(99) > 0)
                {
                    try
                    {
                        await _producerClient.SendAsync(_batchData).ConfigureAwait(false);
                        _pendingItems.Clear();
                        _batchData.Dispose();
                        _batchData = await _producerClient.CreateBatchAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"EventHubClient: SendAsync failed during shutdown: {ex.Message}");
                        // Re-enqueue the failed batch items so the reconnected client can retry them.
                        foreach (var item in _pendingItems)
                        {
                            _logBuffer.Enqueue(item);
                            Interlocked.Increment(ref entryCount);
                        }
                        _pendingItems.Clear();
                        try { await ReconnectAsync(throwOnFailure: false).ConfigureAwait(false); }
                        catch { break; } // give up draining if reconnect also fails
                    }
                }
                else
                {
                    break;
                }
            }

            if (_producerClient is not null)
                await _producerClient.CloseAsync().ConfigureAwait(false);
        }
    }


    private async Task ReconnectAsync(bool throwOnFailure = true, CancellationToken cancellationToken = default)
    {
        async Task ConnectAsync()
        {
            if (!string.IsNullOrEmpty(_config!.ConnectionString))
            {
                _logger.LogInformation("[EVENT HUB] connecting via connection string, eventhubname :" + _config.EventHubName);
                _producerClient = new EventHubProducerClient(_config.ConnectionString, _config.EventHubName);
            }
            else if (!string.IsNullOrEmpty(_config.EventHubNamespace))
            {
                var credential = _defaultCredential.Credential;
                var fullyQualifiedNamespace = _config.EventHubNamespace;
                if (!fullyQualifiedNamespace.EndsWith(".servicebus.windows.net") &&
                    !fullyQualifiedNamespace.EndsWith(".servicebus.usgovcloudapi.net"))
                    fullyQualifiedNamespace = $"{_config.EventHubNamespace}.servicebus.windows.net";
                _producerClient = new EventHubProducerClient(fullyQualifiedNamespace, _config.EventHubName, credential);
            }
        };

        try
        {
            if (_producerClient is not null)
                await _producerClient.CloseAsync().ConfigureAwait(false);
        }
        catch { /* best effort close */ }

        Interlocked.Exchange(ref ReconnectCount, 0);

        for (int attempt = 1; attempt <= _config!.MaxReconnectAttempts; attempt++)
        {
            Interlocked.Increment(ref ReconnectCount);
            try
            {
                await ConnectAsync().ConfigureAwait(false);
                _batchData?.Dispose();
                _batchData = await _producerClient!.CreateBatchAsync().ConfigureAwait(false);
                Console.WriteLine("EventHubClient: Reconnected successfully.");

                Interlocked.Exchange(ref ReconnectCount, 0);
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"EventHubClient: Reconnect failed: {ex.Message}");
                await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false); // Wait for attempt/2 seconds before retrying
            }
        }

        if ( throwOnFailure)
            throw new Exception("EventHubClient: Failed to reconnect after multiple attempts.");
    }

    // Add the log to the batch up to count number at a time
    private int GetNextBatch(int count)
    {
        if (_batchData is null)
            return 0;

        _pendingItems.Clear();
        int initialCount = count;

        for (int i = 0; i < initialCount; i++)
        {
            if (!_logBuffer.TryDequeue(out string? log))
            {
                break;
            }

            EventData eventData;
            try
            {
                eventData = new EventData(Encoding.UTF8.GetBytes(log));
            }
            catch (Exception ex)
            {
                // Drop the item — it cannot be encoded; re-enqueuing would loop forever.
                Interlocked.Decrement(ref entryCount);
                Console.WriteLine($"EventHubClient: Failed to encode log entry, dropping: {ex.Message}");
                continue;
            }

            if (_batchData.TryAdd(eventData))
            {
                Interlocked.Decrement(ref entryCount);
                _pendingItems.Add(log);
            }
            else
            {
                // Item exceeds the EventHub batch size limit — drop it to prevent an infinite re-enqueue cycle.
                Interlocked.Decrement(ref entryCount);
                _logger.LogError("EventHubClient: Log entry too large for batch, dropping ({Bytes} bytes).", Encoding.UTF8.GetByteCount(log));
            }
        }

        return _batchData.Count;
    }

    public void SendData(string? value)
    {
        if (!isRunning || isShuttingDown) return;

        if (value == null) return;

        if (value.StartsWith("\n\n"))
            value = value.Substring(2);

        Interlocked.Increment(ref entryCount);
        _logBuffer.Enqueue(value);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                cancellationTokenSource.Dispose();
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
