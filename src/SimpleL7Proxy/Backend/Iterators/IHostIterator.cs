namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Selects backend hosts while enforcing per-request pass, attempt, and circuit-breaker rules.
/// </summary>
public interface IHostIterator
{
    /// <summary>Gets the number of backend hosts available to this iterator.</summary>
    int HostCount { get; }

    /// <summary>Gets the next eligible backend host for the request.</summary>
    bool TryGet(IterationState state, out BaseHostHealth? host);

    /// <summary>Records one completed backend attempt against the request's attempt budget.</summary>
    void RecordResult(IterationState state, BaseHostHealth host, bool success);
}