using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Events;

public class EventHubClient : IEventClient, IHostedService
{

    private readonly EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private CancellationToken workerCancelToken;
    private bool isRunning = false;
    private bool isShuttingDown = false;
    private Task? writerTask;
    private readonly ConcurrentQueue<string> _logBuffer = new();

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Eventhub Client starting");
        workerCancelToken = cancellationTokenSource.Token;
        if (isRunning && _producerClient is not null && _batchData is not null)
        {
            writerTask = Task.Run(() => EventWriter(workerCancelToken));
            return writerTask;
        }

        return Task.CompletedTask;
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
            while (!token.IsCancellationRequested)
            {
                Console.WriteLine($"EventHubClient: EventWriter running... {isRunning}  {_logBuffer.Count}");
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
                Console.WriteLine("Failed to add log to batchData.");
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
