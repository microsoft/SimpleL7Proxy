namespace SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Traverses a set of hosts in one ordering strategy's sequence for a single lap.
/// Pass/repeat control (SinglePass vs MultiPass, MaxAttempts) lives in <see cref="BaseIterator"/>,
/// not here — MoveNext returning false just means "this lap is done"; Reset starts a new one.
/// </summary>
public interface IHostIterator : IEnumerator<BaseHostHealth>
{
    void RecordResult(BaseHostHealth host, bool success);
    /// <summary>
    /// Gets the total number of hosts in this iterator.
    /// </summary>
    int HostCount { get; }
    IReadOnlyList<BaseHostHealth> Hosts { get; }
}