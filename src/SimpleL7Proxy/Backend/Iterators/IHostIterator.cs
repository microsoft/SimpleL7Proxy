namespace SimpleL7Proxy.Backend.Iterators;

public interface IHostIterator : IEnumerator<BaseHostHealth>
{
    void RecordResult(BaseHostHealth host, bool success);
    bool HasMoreHosts { get; }
    IterationModeEnum Mode { get; }
}