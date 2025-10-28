namespace SimpleL7Proxy.Backend;

public interface IBackendHostIterator : IEnumerator<BackendHostHealth>
{
    void RecordResult(BackendHostHealth host, bool success);
    bool HasMoreHosts { get; }
    IterationModeEnum Mode { get; }
}