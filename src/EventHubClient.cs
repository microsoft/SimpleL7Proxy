using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using Azure.Core;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;


public class EventHubClient : IEventHubClient
{

    private EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private CancellationToken workerCancelToken;
    private volatile bool isRunning = false;
    private volatile bool isShuttingDown = false;
    private Task? writerTask;
    private readonly ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();
    private List<string> _pendingItems = new List<string>();
    // Connection parameters retained for reconnection
    private string? _connectionString;
    private string? _fullyQualifiedNamespace;
    private string? _eventHubName;
    private TokenCredential? _credential;

    public bool IsRunning { get => isRunning; set => isRunning = value; }
    public int GetEntryCount() => entryCount;
    private static int entryCount = 0;
    public static int ReconnectCount = 0;

    public EventHubClient(string fullyQualifiedNamespace, string eventHubName, TokenCredential credential)
    {
        if (string.IsNullOrEmpty(fullyQualifiedNamespace) || string.IsNullOrEmpty(eventHubName))
        {
            isRunning = false;
            _producerClient = null;
            _batchData = null;
            return;
        }

        _fullyQualifiedNamespace = fullyQualifiedNamespace;
        _eventHubName = eventHubName;
        _credential = credential;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _producerClient = new EventHubProducerClient(fullyQualifiedNamespace, eventHubName, credential);
            _batchData = _producerClient.CreateBatchAsync(cts.Token).Result;
            workerCancelToken = cancellationTokenSource.Token;
            isRunning = true;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("EventHubClient setup timed out.");
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to setup EventHubClient.", ex);
        }
    }

    public EventHubClient(string connectionString, string eventHubName)
    {
        if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(eventHubName))
        {
            isRunning = false;
            _producerClient = null;
            _batchData = null;
            return;
        }

        _connectionString = connectionString;
        _eventHubName = eventHubName;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _producerClient = new EventHubProducerClient(connectionString, eventHubName);
            _batchData = _producerClient.CreateBatchAsync(cts.Token).Result;
            workerCancelToken = cancellationTokenSource.Token;
            isRunning = true;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("EventHubClient setup timed out.");
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to setup EventHubClient.", ex);
        }

    }

    public int Count => _logBuffer.Count;

    public bool isHealthy
    {
        get {
            bool healthy = isRunning && !isShuttingDown && ReconnectCount == 0 
                && (writerTask == null || (!writerTask.IsFaulted && !writerTask.IsCanceled))
                && _logBuffer.Count < 50000;
            if (!healthy) {
                Console.WriteLine($"EventHubClient health check failed: isRunning={isRunning}, isShuttingDown={isShuttingDown}, ReconnectCount={ReconnectCount}, writerTaskStatus={(writerTask == null ? "null" : writerTask.Status.ToString())}, logBufferCount={_logBuffer.Count}");
            }
            return healthy;
        }
    }

    public Task StartTimer()
    {

        if (isRunning && _producerClient is not null && _batchData is not null)
        {
            writerTask = Task.Run(() => EventWriter(workerCancelToken));
            return writerTask;
        }

        return Task.CompletedTask;
    }

    public async Task EventWriter(CancellationToken token)
    {
        if (_batchData is null || _producerClient is null)
            return;


        try
        {

            while (!token.IsCancellationRequested)
            {
                if (GetNextBatch(99) > 0)
                {
                    try
                    {
                        await _producerClient.SendAsync(_batchData).ConfigureAwait(false);
                        _pendingItems.Clear();
                        _batchData.Dispose();
                        _batchData = await _producerClient.CreateBatchAsync(token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.WriteLine($"EventHubClient: SendAsync failed, reconnecting: {ex.Message}");
                        // Re-enqueue items from the failed batch so they are not lost
                        foreach (var item in _pendingItems)
                        {
                            _logBuffer.Enqueue(item);
                            Interlocked.Increment(ref entryCount);
                        }
                        _pendingItems.Clear();
                        try { await ReconnectAsync(cancellationToken: token).ConfigureAwait(false); }
                        catch { /* reconnect failed; will retry on next iteration */ }
                        continue;
                    }
                }

                if (!isShuttingDown) {
                    await Task.Delay(500, token).ConfigureAwait(false); // Wait for 1/2 second
                }
            }
            Console.WriteLine("EventHubClient: EventWriter exiting");

        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via cancellation token
        }
        finally
        {

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
                        foreach (var item in _pendingItems)
                        {
                            _logBuffer.Enqueue(item);
                            Interlocked.Increment(ref entryCount);
                        }
                        _pendingItems.Clear();
                        try { await ReconnectAsync().ConfigureAwait(false); }
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
        isRunning = false;
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
                _logBuffer.Enqueue(log);
                Console.WriteLine("Failed to add log to batchData.");
            }
        }

        return _batchData.Count;
    }

    public async Task StopTimer()
    {
        isShuttingDown = true;
        var drainDeadline = DateTime.UtcNow.AddSeconds(30);
        while (isRunning && _logBuffer.Count > 0 && DateTime.UtcNow < drainDeadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        if (_logBuffer.Count > 0)
            Console.WriteLine($"[SHUTDOWN] EventHubClient stopped with {_logBuffer.Count} items still in queue.");

        cancellationTokenSource.Cancel();
        isRunning = false;

        if (writerTask != null)
            await writerTask.ConfigureAwait(false);
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

    public void SendData(ProxyEvent eventData)
    {
        if (!isRunning || isShuttingDown) return;

        eventData["Ver"] = Constants.VERSION;
        eventData["Revision"] = Constants.REVISION;
        eventData["ContainerApp"] = Constants.CONTAINERAPP;

        string jsonData = JsonSerializer.Serialize(eventData);
        SendData(jsonData);
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_producerClient is not null)
                await _producerClient.CloseAsync().ConfigureAwait(false);
        }
        catch { /* best effort close */ }

        Interlocked.Exchange(ref ReconnectCount, 0);

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            Interlocked.Increment(ref ReconnectCount);
            try
            {
                if (!string.IsNullOrEmpty(_connectionString))
                    _producerClient = new EventHubProducerClient(_connectionString, _eventHubName);
                else if (!string.IsNullOrEmpty(_fullyQualifiedNamespace) && _credential != null)
                    _producerClient = new EventHubProducerClient(_fullyQualifiedNamespace, _eventHubName, _credential);
                else
                    throw new InvalidOperationException("No connection parameters available for reconnect.");

                _batchData?.Dispose();
                _batchData = await _producerClient!.CreateBatchAsync().ConfigureAwait(false);
                Console.WriteLine("EventHubClient: Reconnected successfully.");
                Interlocked.Exchange(ref ReconnectCount, 0);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"EventHubClient: Reconnect attempt {attempt} failed: {ex.Message}");
                if (attempt < 5)
                    await Task.Delay(500 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new Exception("EventHubClient: Failed to reconnect after multiple attempts.");
    }
}