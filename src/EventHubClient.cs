using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;


public class EventHubClient : IEventHubClient
{

    private EventHubProducerClient? _producerClient;
    private EventDataBatch? _batchData;
    private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
    private bool _isRunning = false;
    private ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

    public EventHubClient(string connectionString, string eventHubName)
    {
        if (connectionString != "" && eventHubName != "")
        {
            _producerClient = new EventHubProducerClient(connectionString, eventHubName);
            _batchData = _producerClient.CreateBatchAsync().Result;
            _isRunning = true;
        }
    }

    public void StartTimer()
    {
        if (_isRunning && _producerClient is not null && _batchData is not null)
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
        while (_logBuffer.TryDequeue(out string? log) && count-- > 0)
        {
            _batchData.TryAdd(new EventData(Encoding.UTF8.GetBytes(log)));
        }

        return _batchData.Count;
    }

    public void StopTimer()
    {
        if (_isRunning)
            _cancellationTokenSource?.Cancel();
        _isRunning = false;
    }

    public void SendData(string? value)
    {
        if (!_isRunning) return;

        if (value == null) return;

        if (value.StartsWith("\n\n")) 
            value = value.Substring(2);
        
        _logBuffer.Enqueue(value);
    }

    public void SendData(Dictionary<string, string> eventData) {

        string jsonData = JsonSerializer.Serialize(eventData);
        SendData(jsonData);
    }

}