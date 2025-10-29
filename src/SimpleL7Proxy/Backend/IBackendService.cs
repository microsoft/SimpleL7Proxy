namespace SimpleL7Proxy.Backend;

/// <summary>
/// Interface for backend services supporting both DirectBackend and APIMBackend types.
/// </summary>
public interface IBackendService
{
    List<BaseHostHealth> GetHosts();
    List<BaseHostHealth> GetActiveHosts();
    int ActiveHostCount();
    // BackendType BackendKind { get; }
    string HostStatus { get; }
    void TrackStatus(int code, bool wasException);
    bool CheckFailedStatus();
    // string OAuth2Token();
    Task WaitForStartup(int timeout);
    void Start();
    Task Stop();
    IBackendHostIterator GetHostIterator(string loadBalanceMode, IterationModeEnum mode = IterationModeEnum.SinglePass, int maxRetries = 1);
}

public enum BackendType
{
  DirectBackend,
  APIMBackend
}

