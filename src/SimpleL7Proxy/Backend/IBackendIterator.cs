namespace SimpleL7Proxy.Backend;

public interface IBackendHostIterator : IEnumerator<BaseHostHealth>
{
    void RecordResult(BaseHostHealth host, bool success);
    bool HasMoreHosts { get; }
    IterationModeEnum Mode { get; }
}