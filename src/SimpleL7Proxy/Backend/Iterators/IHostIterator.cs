namespace SimpleL7Proxy.Backend.Iterators;

public interface IHostIterator : IEnumerator<BaseHostHealth>
{
    void RecordResult(BaseHostHealth host, bool success);
    bool HasMoreHosts { get; }
    IterationModeEnum Mode { get; }
    /// <summary>
    /// Gets the total number of hosts in this iterator.
    /// </summary>
    int HostCount { get; }
}