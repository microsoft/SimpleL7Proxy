using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class LogFileEventClient : IEventClient, IHostedService
{

    private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    private CancellationToken workerCancelToken;
    private bool isRunning = false;
    private bool isShuttingDown = false;
    private bool beginShutdown = false;
    private Task? writerTask;
    private ConcurrentQueue<string> _logBuffer = new ConcurrentQueue<string>();

    public bool IsRunning { get => isRunning; set => isRunning = value; }
    public int GetEntryCount() => entryCount;
    private static int entryCount = 0;

    private readonly CompositeEventClient _composite;
    private readonly StringBuilder _sb = new();
    private static Stream log = null!;
    private static StreamWriter writer = null!;
    
    public LogFileEventClient(string filename, CompositeEventClient composite, IOptions<ProxyConfig> options )
    {
        _composite = composite ?? throw new ArgumentNullException(nameof(composite));
        // create file stream to a log file
        log = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write);
        writer = new StreamWriter(log)
        {
            AutoFlush = true
        };

        workerCancelToken = cancellationTokenSource.Token; 


        return;
    }

    public int Count => _logBuffer.Count;
    public string ClientType => "LogFile";

    public bool IsHealthy()
    {
        return isRunning && !isShuttingDown;
    }


    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[STARTUP] ✓ Local File Logger starting");
        workerCancelToken = cancellationTokenSource.Token;
        if (!isRunning)
        {
            _composite.Add(this);
            writerTask = Task.Run(() => EventWriter(workerCancelToken));
        }
        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        beginShutdown = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopTimerAsync().ConfigureAwait(false);
    }


    public async Task EventWriter(CancellationToken token)
    {
        isRunning = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                LogNextBatch(99);

                if (!beginShutdown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false); // Wait for 1/2 second
                }
            }
            Console.WriteLine("[SHUTDOWN] ✓ LogFileEventClient exiting");

        }
        catch (TaskCanceledException)
        {
            // Ignore
        }
        finally
        {
            while (true)
            {
                if (LogNextBatch(99) == 0)
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
    private int LogNextBatch(int count)
    {
        _sb.Clear();
        int drained = 0;

        while (drained < count && _logBuffer.TryDequeue(out string? line))
        {
            _sb.AppendLine(line);
            drained++;
        }

        if (drained > 0)
        {
            writer.Write(_sb);
            writer.Flush();
            Interlocked.Add(ref entryCount, -drained);
        }
        return drained;
    }

    public async Task StopTimerAsync()
    {
        if (writerTask == null)
        {
            Console.WriteLine("LogFileEventClient: StopTimerAsync called but writerTask is null");
            return;
        }
        isShuttingDown = true;
        while (isRunning && _logBuffer.Count > 0)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }
        cancellationTokenSource.Cancel();

        await writerTask.ConfigureAwait(false);

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
}