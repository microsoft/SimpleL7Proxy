using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

public class LogFileEventClient : IEventClient, IHostedService
{

    private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private CancellationToken workerCancelToken;
    private bool isRunning = false;
    private bool isShuttingDown = false;
    private Task? writerTask;
    private ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

    public bool IsRunning { get => isRunning; set => isRunning = value; }
    public int GetEntryCount() => entryCount;
    private static int entryCount = 0;

    private static Stream log = null!;
    private static StreamWriter writer = null!;
    public LogFileEventClient(string filename)
    {
        // create file stream to a log file
        log = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write);
        writer = new StreamWriter(log)
        {
            AutoFlush = true
        };

        workerCancelToken = cancellationTokenSource.Token; 

        isRunning = true;

        return;
    }

    public int Count => _logBuffer.Count;


    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Local File Logger starting");
        workerCancelToken = cancellationTokenSource.Token;
        if (isRunning)
        {
            writerTask = Task.Run(() => EventWriter(workerCancelToken));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopTimer();
        return Task.CompletedTask;
    }


    public async Task EventWriter(CancellationToken token)
    {
        try
        {

            while (!token.IsCancellationRequested)
            {
                LogNextBatch(99);

                if (!isShuttingDown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false); // Wait for 1/2 second
                }
            }
            Console.WriteLine("LogFileEventClient: EventWriter exiting");

        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        finally
        {
            while (true)
            {
                LogNextBatch(99);
                if (_logBuffer.Count == 0)
                    break;
            }

            await Task.Delay(500).ConfigureAwait(false); // Wait for 1/2 second
                                                         // make sure event hub client is closed

            writer.Flush();
            writer.Dispose();
            log?.Close();
            log?.Dispose();
        }
    }

    // Add the log to the batch up to count number at a time
    private void LogNextBatch(int count)
    {
        int initialCount = count;

        for (int i = 0; i < initialCount; i++)
        {
            if (!_logBuffer.TryDequeue(out string? log))
            {
                break;
            }

            writer.WriteLine(log);
            Interlocked.Decrement(ref entryCount);
        }

        writer.Flush();
    }

    public void StopTimer()
    {
        if (writerTask == null)
        {
            Console.WriteLine("LogFileEventClient: StopTimer called but writerTask is null");
            return;
        }
        isShuttingDown = true;
        while (isRunning && _logBuffer.Count > 0)
        {
            Task.Delay(100).Wait();
        }
        cancellationTokenSource.Cancel();

        writerTask?.Wait();

        isRunning = false;
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

    // public void SendData(Dictionary<string, string> eventData)
    // {
    //     if (!isRunning || isShuttingDown) return;

    //     SendData(JsonSerializer.Serialize(eventData));
    // }
    
    // public void SendData( ConcurrentDictionary<string, string> eventData, string? name = null)
    // {
    //     if (!isRunning || isShuttingDown) return;

    //     SendData(JsonSerializer.Serialize(eventData));
    // }
    public void SendData(ProxyEvent proxyEvent)
    {
        if (!isRunning || isShuttingDown) return;

        string jsonData = JsonSerializer.Serialize(proxyEvent);
        SendData(jsonData);
    }

}