using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy;

// Implement to participate. Call RegisterReady() when initialized; RegisterNotReady() if you degrade.
public interface IReadinessParticipant
{
    ReadinessParticipantEnum Participant { get; }
    ReadinessRegistry Readiness { get; }
}

public static class ReadinessParticipantExtensions
{
    public static void RegisterReady(this IReadinessParticipant p)    => p.Readiness.MarkReady(p.Participant);
    public static void RegisterNotReady(this IReadinessParticipant p) => p.Readiness.MarkNotReady(p.Participant);
}

// Components that gate system readiness. Composite readiness = all registered gates ready.
public enum ReadinessParticipantEnum
{
    // Always required
    Backends, BackendTokens, Workers, UserProfiles, EventClient,

    // Async mode only
    AsyncTemplates, BlobWriter, SBQueue, SBTopic,
}

public static class ReadinessParticipantInfo
{
    public static bool IsAsyncOnly(ReadinessParticipantEnum p) =>
        p is ReadinessParticipantEnum.AsyncTemplates
          or ReadinessParticipantEnum.BlobWriter
          or ReadinessParticipantEnum.SBQueue
          or ReadinessParticipantEnum.SBTopic;
}


// Bootstrap-only readiness gate registry. Owned by DI as a singleton.
// Pre-seeds the expected participants so AllReady() can't return true prematurely.
public sealed class ReadinessRegistry
{
    private readonly ILogger<ReadinessRegistry> _logger;
    private readonly int[] _state;       // 0 = not ready, 1 = ready, indexed by (int)enum
    private readonly bool[] _expected;   // true for participants required by config
    private readonly int _expectedCount;
    private int _readyCount;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReadinessRegistry(IOptions<ProxyConfig> options, ILogger<ReadinessRegistry> logger)
    {
        _logger = logger;
        var values = Enum.GetValues<ReadinessParticipantEnum>();
        _state = new int[values.Length];
        _expected = new bool[values.Length];
        var asyncEnabled = options.Value.AsyncModeEnabled;
        foreach (var p in values)
        {
            if (ReadinessParticipantInfo.IsAsyncOnly(p) && !asyncEnabled) continue;
            _expected[(int)p] = true;
            _expectedCount++;
        }
    }

    public void MarkReady(ReadinessParticipantEnum p)
    {
        int idx = (int)p;
        if (Interlocked.Exchange(ref _state[idx], 1) != 0) return;

         _logger.LogInformation("[GATE] \u2713 {Name} ready", p);
        if (_expected[idx]
            && Interlocked.Increment(ref _readyCount) == _expectedCount
            && _ready.TrySetResult())
        {
            _logger.LogInformation("[GATE] \u2713 All participants ready");
        }
    }

    public void MarkNotReady(ReadinessParticipantEnum p)
    {
        int idx = (int)p;
        if (Interlocked.Exchange(ref _state[idx], 0) != 1) return;

        _logger.LogWarning("[GATE] \u25cb {Name} not ready", p);
        if (_expected[idx]) Interlocked.Decrement(ref _readyCount);
    }

    // Awaited by services that must not start until everything is ready.
    public Task WaitForReadyAsync() => _ready.Task;

    // (participant, isReady) snapshot for diagnostics.
    public IReadOnlyCollection<(ReadinessParticipantEnum Participant, bool IsReady)> Snapshot()
    {
        var list = new List<(ReadinessParticipantEnum, bool)>(_expectedCount);
        foreach (var p in Enum.GetValues<ReadinessParticipantEnum>())
            if (_expected[(int)p])
                list.Add((p, Volatile.Read(ref _state[(int)p]) == 1));
        return list;
    }
}

// Hosted service that marks a participant ready on startup. Used when a participant
// has no natural ready event (e.g. disabled subsystems that still must close their gate).
internal sealed class ReadinessMarker : IHostedService
{
    private readonly IReadinessParticipant _participant;
    public ReadinessMarker(IReadinessParticipant participant) => _participant = participant;
    public Task StartAsync(CancellationToken _) { _participant.RegisterReady(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken _) => Task.CompletedTask;
}
