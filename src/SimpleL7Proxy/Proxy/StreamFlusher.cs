using SimpleL7Proxy.Config;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;

namespace SimpleL7Proxy.Proxy;

public class StreamFlusher : IHostedService
{
    private readonly ProxyConfig _options;
    private readonly TimeSpan s_flushInterval;

    private Timer? _flushTimer;
    private int _isFlushing;
    private Stream[] _snapshot = [];
    private int _isDirty = 1;

    private readonly ConcurrentDictionary<Stream, byte> _streams = new();

    public StreamFlusher(ProxyConfig options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        s_flushInterval = TimeSpan.FromMilliseconds(_options.StreamFlushInterval);

    }

    public bool AddStream(Stream buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!_streams.TryAdd(buffer, 0))
        {
            return false;
        }

        Interlocked.Exchange(ref _isDirty, 1);
        return true;
    }

    public bool RemoveStream(Stream buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!_streams.TryRemove(buffer, out _))
        {
            return false;
        }

        Interlocked.Exchange(ref _isDirty, 1);
        return true;
    }

    public int Count => _streams.Count;

    private void FlushBuffers()
    {
        // Prevent timer re-entrancy if a prior flush pass takes longer than the interval.
        if (Interlocked.Exchange(ref _isFlushing, 1) == 1)
        {
            return;
        }

        try
        {
            if (Interlocked.Exchange(ref _isDirty, 0) == 1)
            {
                var next = _streams.Keys.ToArray();
                Volatile.Write(ref _snapshot, next);
            }

            var snapshot = Volatile.Read(ref _snapshot);

            foreach (var stream in snapshot)
            {
                try
                {
                    if (stream.CanWrite)
                    {
                        stream.Flush();
                    }
                }
                catch (Exception)
                {
                    // Best-effort flushing; failures are isolated to each stream.
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isFlushing, 0);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Single timer for periodic best-effort flushing.
        _flushTimer = new Timer(_ => FlushBuffers(), null, s_flushInterval, s_flushInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        var timer = Interlocked.Exchange(ref _flushTimer, null);
        if (timer == null)
        {
            return Task.CompletedTask;
        }

        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
        return Task.CompletedTask;
    }

    public Task OnConfigChangedAsync(IReadOnlyList<ConfigChange> changes, ProxyConfig backendOptions, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }


}