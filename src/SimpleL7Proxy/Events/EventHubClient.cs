using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Events;

public class EventHubClient : IEventClient, IHostedService
{

    private readonly EventHubConfig? _config;
    private EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private readonly ILogger<EventHubClient> _logger;
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

    public EventHubClient(EventHubConfig config, ILogger<EventHubClient> logger)
    {
        _config = config;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if ( (!string.IsNullOrEmpty(config.ConnectionString) && !string.IsNullOrEmpty(config.EventHubName)) ||
             (!string.IsNullOrEmpty(config.EventHubNamespace) && !string.IsNullOrEmpty(config.EventHubName))
           )
        {
            // we will try to start it up
            return;
        }

        // We know that it won't be running.
        isRunning = false;
        _producerClient = null;
        _batchData = null;
        return;
        
    }

    public int Count => _logBuffer.Count;

    public async Task StartAsync(CancellationToken cancellationToken) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try {
            if (!string.IsNullOrEmpty(_config?.ConnectionString))
            {

                _producerClient = new EventHubProducerClient(_config.ConnectionString, _config.EventHubName);
            }
            else if ( !string.IsNullOrEmpty(_config?.EventHubNamespace) && !string.IsNullOrEmpty(_config?.EventHubName) )
            {
                var fullyQualifiedNamespace = _config.EventHubNamespace;
                if (!fullyQualifiedNamespace.EndsWith(".servicebus.windows.net"))
                    fullyQualifiedNamespace = $"{_config?.EventHubNamespace}.servicebus.windows.net";
            
                _producerClient = new EventHubProducerClient(fullyQualifiedNamespace, _config?.EventHubName, new Azure.Identity.DefaultAzureCredential());
            } else
            {
                _logger.LogError("EventHubClient configuration is invalid. Missing connection information.");
                throw new InvalidOperationException("EventHubClient configuration is invalid. Missing connection information.");
            }
            
            _batchData = await _producerClient.CreateBatchAsync(cts.Token).ConfigureAwait(false);
            workerCancelToken = cancellationTokenSource.Token;
            isRunning = true;
        }
        catch (OperationCanceledException) {
            _logger.LogError("EventHubClient setup timed out");
            throw new TimeoutException("EventHubClient setup timed out.");
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Failed to setup EventHubClient");
            throw new Exception("Failed to setup EventHubClient.", ex);
        }

        _logger.LogCritical("[SERVICE] ✓ EventHub Client starting");
        if (isRunning && _producerClient is not null && _batchData is not null) {
            writerTask = Task.Run(() => EventWriter(workerCancelToken), workerCancelToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopTimer();
        return Task.CompletedTask;
    }

    TaskCompletionSource<bool> ShutdownTCS = new();

    public void StopTimer()
    {
        isShuttingDown = true;
        while (isRunning && _logBuffer.Count > 0)
        {
            Task.Delay(100).Wait();
        }

        cancellationTokenSource.Cancel();
        isRunning = false;
        writerTask?.Wait();
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

            if (_batchData.TryAdd(new EventData(Encoding.UTF8.GetBytes(log))))
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

    // public void SendData(Dictionary<string, string> data)
    // {
    //     if (!isRunning || isShuttingDown) return;

    //     string jsonData = JsonSerializer.Serialize(data);
    //     SendData(jsonData);
    // }

    public void SendData(ProxyEvent proxyEvent)
    {
        if (!isRunning || isShuttingDown) return;

        string jsonData = JsonSerializer.Serialize(proxyEvent);
        SendData(jsonData);
    }

    // public void SendData(ConcurrentDictionary<string, string> eventData, string? name = null)
    // {
    //     if (!isRunning || isShuttingDown) return;

    //     string jsonData = JsonSerializer.Serialize(eventData);
    //     SendData(jsonData);
    // }
}
