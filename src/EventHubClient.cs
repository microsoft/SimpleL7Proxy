using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;


public class EventHubClient : IEventHubClient
{

    private EventHubProducerClient? producerClient;
    private EventDataBatch? batchData;
    private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private bool isRunning = false;
    private ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

    public EventHubClient(string connectionString, string eventHubName)
    {
        if (string.IsNullOrEmpty(connectionString) || string.IsNullOrEmpty(eventHubName))
        {
            isRunning = false;
            producerClient = null;
            batchData = null;
            return;
        }

        producerClient = new EventHubProducerClient(connectionString, eventHubName);
        batchData = producerClient.CreateBatchAsync().Result;
        isRunning = true;
    }

    public void StartTimer()
    {
        if (isRunning && producerClient is not null && batchData is not null)
            Task.Run(() => WriterTask());
    }

    public async Task WriterTask()
    {
        if (batchData is null || producerClient is null)
            return;
            
        try {

            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (GetNextBatch(100) > 0)
                {
                    await producerClient.SendAsync(batchData);
                    batchData = await producerClient.CreateBatchAsync();
                }

                await Task.Delay(1000, cancellationTokenSource.Token); // Wait for 1 second
            }

        } catch (TaskCanceledException) {
            // Ignore
        } finally {

            while (true) {
                if (GetNextBatch(100) > 0)
                {
                    await producerClient.SendAsync(batchData);
                } else {
                    break;
                }
            }
        }
    }

    // Add the log to the batch up to count number at a time
    private int GetNextBatch(int count)
    {
        if (batchData is null)
          return 0;

        while (_logBuffer.TryDequeue(out string? log) && count-- > 0)
        {
            batchData.TryAdd(new EventData(Encoding.UTF8.GetBytes(log)));
        }

        return batchData.Count;
    }

    public void StopTimer()
    {
        if (isRunning)
            cancellationTokenSource?.Cancel();
        isRunning = false;
    }

    public void SendData(string? value)
    {
        if (!isRunning) return;

        if (value == null) return;

        if (value.StartsWith("\n\n")) 
            value = value.Substring(2);
        
        _logBuffer.Enqueue(value);
    }

    public void SendData(Dictionary<string, string> eventData) {
        if (!isRunning) return;

        string jsonData = JsonSerializer.Serialize(eventData);
        SendData(jsonData);
    }

}