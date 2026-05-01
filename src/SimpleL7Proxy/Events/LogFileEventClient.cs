using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;


using SimpleL7Proxy.Config;
using SimpleL7Proxy.Messaging;

namespace SimpleL7Proxy.Events;

public class LogFileEventClient : IEventClient, IHostedService, IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>
{
    private IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope> BatchTransport => this;

    private bool isRunning = false;
    private readonly BatchMessagePump<List<BatchMessageEnvelope>, BatchMessageEnvelope> _pump;
    private const string DefaultDestination = "file";

    public bool IsRunning { get => _pump.IsRunning || isRunning; set => isRunning = value; }
    public int GetEntryCount() => _pump.EntryCount;

    private readonly CompositeEventClient _composite;
    private readonly StringBuilder _sb = new();
    private static Stream log = null!;
    private static StreamWriter writer = null!;

    public LogFileEventClient(string filename, CompositeEventClient composite, IOptions<ProxyConfig> options)
    {
        var proxyOptions = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _composite = composite ?? throw new ArgumentNullException(nameof(composite));

        log = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write);
        writer = new StreamWriter(log)
        {
            AutoFlush = true,
        };

        _pump = new BatchMessagePump<List<BatchMessageEnvelope>, BatchMessageEnvelope>(
            destination: DefaultDestination,
            transport: this,
            createBatchAsync: cancellationToken => BatchTransport.CreateBatchAsync(DefaultDestination, cancellationToken),
            recoverBatchAsync: cancellationToken => BatchTransport.CreateBatchAsync(DefaultDestination, cancellationToken),
            options: new BatchMessagePumpOptions
            {
                FlushCountThreshold = 10,
                FlushInterval = TimeSpan.FromSeconds(2),
                WaitThreshold = proxyOptions.MaxUndrainedEvents / 4,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
            });
    }

    public int Count => _pump.Count;
    public int FlushedLastMinute => _pump.FlushedLastMinute;
    public string ClientType => "LogFile";

    public bool IsHealthy()
    {
        return _pump.IsRunning && !_pump.IsShuttingDown;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[STARTUP] ✓ Local File Logger starting");
        if (!_pump.IsRunning)
        {
            await _pump.StartAsync(cancellationToken).ConfigureAwait(false);
            _composite.Add(this);
            isRunning = true;
        }
    }

    public void BeginShutdown()
    {
        _pump.BeginShutdown();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // Shutdown is owned by CompositeEventClient to preserve ordering during coordinated stop.
        return Task.CompletedTask;
    }

    public async Task StopTimerAsync()
    {
        if (!_pump.IsRunning && !isRunning)
        {
            Console.WriteLine("LogFileEventClient: StopTimerAsync called but the logger is already stopped.");
            return;
        }

        await _pump.StopAsync().ConfigureAwait(false);

        isRunning = false;

        if (_pump.Count > 0)
        {
            Console.WriteLine($"[SHUTDOWN] LogFileEventClient stopped with {_pump.Count} items still in queue.");
        }
    }

    public void SendData(string? value)
    {
        if (value != null)
        {
            _pump.Enqueue(new BatchMessageEnvelope(DefaultDestination, value));
        }
    }

    Task IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.OpenAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    ValueTask<List<BatchMessageEnvelope>> IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.CreateBatchAsync(string destination, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new List<BatchMessageEnvelope>());
    }

    bool IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.TryAdd(List<BatchMessageEnvelope> batch, BatchMessageEnvelope message)
    {
        batch.Add(message);
        return true;
    }

    int IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.GetCount(List<BatchMessageEnvelope> batch)
    {
        return batch.Count;
    }

    Task IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.SendAsync(string destination, List<BatchMessageEnvelope> batch, CancellationToken cancellationToken)
    {
        _sb.Clear();
        foreach (var message in batch)
        {
            _sb.AppendLine(message.Payload);
        }

        writer.Write(_sb);
        writer.Flush();
        return Task.CompletedTask;
    }

    void IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.DisposeBatch(List<BatchMessageEnvelope> batch)
    {
        batch.Clear();
    }

    Task IBatchMessageTransport<List<BatchMessageEnvelope>, BatchMessageEnvelope>.CloseAsync(CancellationToken cancellationToken)
    {
        writer.Flush();
        writer.Dispose();
        log?.Close();
        log?.Dispose();
        return Task.CompletedTask;
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