namespace SimpleL7Proxy.Backend.Iterators;

using Microsoft.Extensions.Logging;

/// <summary>
/// Per-request attempt/circuit-breaker state consumed by <see cref="BaseIterator.TryGet"/>.
/// Must never be stored on a <see cref="BaseIterator"/> instance itself — a
/// <see cref="SharedHostIterator"/> is one instance shared by every concurrent request to a
/// path, so each request needs its own copy of this state.
/// </summary>
public sealed class IterationState
{
    private readonly IterationModeEnum _mode;

    internal IterationState(IterationModeEnum mode, int maxAttempts, ILogger? logger = null, int priorAttempts = 0)
    {
        _mode = mode;
        MaxAttempts = maxAttempts;
        Logger = logger;
        // Seeded from the request's lifetime attempt count so MaxAttempts is a true
        // ceiling across requeue cycles, not just within the current one (which the
        // requeue worker resets BackendAttempts on).
        TotalAttempts = priorAttempts;
    }

    internal IterationModeEnum Mode => _mode;
    internal int MaxAttempts { get; }
    internal ILogger? Logger { get; }
    internal int TotalAttempts { get; set; }
    internal BaseHostHealth? AvailableCircuitBreakerHost { get; set; }
    internal IReadOnlyList<BaseHostHealth>? CircuitBreakerHosts { get; set; }
}
