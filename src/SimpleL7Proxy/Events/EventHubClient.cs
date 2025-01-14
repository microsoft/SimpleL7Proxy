using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace SimpleL7Proxy.Events;
public class EventHubClient : IEventClient
{

    private readonly EventHubProducerClient _producerClient;
    private EventDataBatch _batchData;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool isRunning = false;
    private readonly ConcurrentQueue<string> _logBuffer = new();

    public EventHubClient(string connectionString, string eventHubName)
    {
        _producerClient = new EventHubProducerClient(connectionString, eventHubName);
        _batchData = _producerClient.CreateBatchAsync().Result; //TODO: Don't await in constructors.
        isRunning = true;
    }

    public void StartTimer()
    {
        if (isRunning && _producerClient is not null && _batchData is not null)
            Task.Run(() => WriterTask());
    }

    public async Task WriterTask()
    {
        if (_batchData is null || _producerClient is null)
            return;
            
        try {

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (GetNextBatch(100) > 0)
                {
                    await _producerClient.SendAsync(_batchData);
                    _batchData = await _producerClient.CreateBatchAsync();
                }

                await Task.Delay(1000, _cancellationTokenSource.Token); // Wait for 1 second
            }

        } catch (TaskCanceledException) {
            // Ignore
        } finally {

            while (true) {
                if (GetNextBatch(100) > 0)
                {
                    await _producerClient.SendAsync(_batchData);
                } else {
                    break;
                }
            }
        }
    }

    // Add the log to the batch up to count number at a time
    private int GetNextBatch(int count)
    {
        if (_batchData is null)
          return 0;

        while (_logBuffer.TryDequeue(out string? log) && count-- > 0)
        {
            _batchData.TryAdd(new EventData(Encoding.UTF8.GetBytes(log)));
        }

        return _batchData.Count;
    }

    public void StopTimer()
    {
        if (isRunning)
            _cancellationTokenSource.Cancel();
        isRunning = false;
    }

    public void SendData(string? value)
    {
        if (!isRunning || value == null) return;

        if (value.StartsWith("\n\n")) 
            value = value[2..];
        
        _logBuffer.Enqueue(value);
    }

    public void SendData(ProxyEvent proxyEvent) {
        if (!isRunning) return;

        string jsonData = JsonSerializer.Serialize(proxyEvent.EventData);
        SendData(jsonData);
    }
}
