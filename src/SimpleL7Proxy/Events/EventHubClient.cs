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
    private bool isRunning = false;
    private bool isShuttingDown = false;
    private Task? writerTask;
    private readonly ConcurrentQueue<string> _logBuffer = new();

    public bool IsRunning { get => isRunning; set => isRunning = value; }
    public int GetEntryCount() => entryCount;
    private static int entryCount = 0;
    //public EventHubClient(string connectionString, string eventHubName, ILogger<EventHubClient>? logger = null)

    public EventHubClient(CompositeEventClient composite, 
        IOptions<BackendOptions> options, 
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

    public async Task StartAsync(CancellationToken cancellationToken) {
        // If config failed to initialize (constructor threw), skip startup gracefully
        if (_config == null)
        {
            _logger.LogInformation("EventHubClient configuration is null. EventHub will not be started.");
            isRunning = false;
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.StartupSeconds));
        try {
            if (!string.IsNullOrEmpty(_config.ConnectionString))
            {

                _logger.LogInformation("[EVENT HUB] connecting via connection string, eventhubname :" + _config.EventHubName  );
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
            
            _batchData = await _producerClient!.CreateBatchAsync(cts.Token).ConfigureAwait(false);
            workerCancelToken = cancellationTokenSource.Token;
            isRunning = true;
            
            _composite.Add(this);
            _logger.LogCritical("[SERVICE] ✓ EventHub Client started successfully");
            writerTask = Task.Run(() => EventWriter(workerCancelToken), workerCancelToken);
        }
        catch (OperationCanceledException) {
            _logger.LogError("EventHubClient setup timed out after {Seconds} seconds. EventHub logging will be disabled.", _config.StartupSeconds);
            isRunning = false;
            // Don't throw — other event clients (e.g. LogFileEventClient) should continue running
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to setup EventHubClient. EventHub logging will be disabled.");
            isRunning = false;
            // Don't throw — other event clients (e.g. LogFileEventClient) should continue running
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopTimerAsync().ConfigureAwait(false);
    }

    TaskCompletionSource<bool> ShutdownTCS = new();

    public async Task StopTimerAsync()
    {
        isShuttingDown = true;
        while (isRunning && _logBuffer.Count > 0)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        cancellationTokenSource.Cancel();
        isRunning = false;
        if (writerTask != null)
            await writerTask.ConfigureAwait(false);
    }

    public async Task EventWriter(CancellationToken token)
    {
        if (_batchData is null || _producerClient is null)
            return;

        try
        {
            _logger.LogCritical($"EventHubClient: EventWriter running.");
            while (!token.IsCancellationRequested)
            {
                if (GetNextBatch(99) > 0)
                {
                    await _producerClient.SendAsync(_batchData).ConfigureAwait(false);
                    _batchData.Dispose();
                    _batchData = await _producerClient.CreateBatchAsync().ConfigureAwait(false);
                }

                if (!isShuttingDown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false); // Wait for 1/2 second
                }
            }
            _logger.LogInformation("[SHUTDOWN] ✓ EventHubClient exiting");

        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        finally
        {

            while (true)
            {
                if (GetNextBatch(99) > 0)
                {
                    await _producerClient.SendAsync(_batchData).ConfigureAwait(false);
                    _batchData.Dispose();
                    _batchData = await _producerClient.CreateBatchAsync().ConfigureAwait(false);
                }
                else
                {
                    break;
                }
            }

            await Task.Delay(500).ConfigureAwait(false); // Wait for 1/2 second
            // make sure event hub client is closed
            await _producerClient.CloseAsync().ConfigureAwait(false);
        }
    }

    // Add the log to the batch up to count number at a time
    private int GetNextBatch(int count)
    {
        if (_batchData is null)
            return 0;

        int initialCount = count;

        for (int i = 0; i < initialCount; i++)
        {
            if (!_logBuffer.TryDequeue(out string? log))
            {
                break;
            }

            var eventData = new EventData(Encoding.UTF8.GetBytes(log));
            if (_batchData.TryAdd(eventData))
            {
                Interlocked.Decrement(ref entryCount);
            }
            else
            {
                _logBuffer.Enqueue(log);
                _logger.LogError("Failed to add log to batchData.");
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
        //Console.WriteLine($" Enqueued: {value}");
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
