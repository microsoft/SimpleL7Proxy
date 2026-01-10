using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;


public class EventHubClient : IEventHubClient
{

    private EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private CancellationToken workerCancelToken;
    private bool isRunning = false;
    private bool isShuttingDown = false;
    private Task? writerTask;
    private ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

    public bool IsRunning { get => isRunning; set => isRunning = value; }
    public int GetEntryCount() => entryCount;
    private static int entryCount = 0;

    public EventHubClient(string connectionString, string eventHubName)
    {
        if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(eventHubName))
        {
            isRunning = false;
            _producerClient = null;
            _batchData = null;
            return;
        }

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
                    await _producerClient.SendAsync(_batchData).ConfigureAwait(false);
                    _batchData.Dispose();
                    _batchData = await _producerClient.CreateBatchAsync().ConfigureAwait(false);
                }

                if (!isShuttingDown) {
                    await Task.Delay(500, token).ConfigureAwait(false); // Wait for 1/2 second
                }
            }
            Console.WriteLine("EventHubClient: EventWriter exiting");

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
                Console.WriteLine("Failed to add log to batchData.");
            }
        }

        return _batchData.Count;
    }

    public Task StopTimer()
    {
        isShuttingDown = true;
        while (isRunning && _logBuffer.Count > 0)
        {
            Task.Delay(100).Wait();
        }

        cancellationTokenSource.Cancel();
        isRunning = false;

        return writerTask!;
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

        string jsonData = JsonSerializer.Serialize(eventData);
        SendData(jsonData);
    }

}