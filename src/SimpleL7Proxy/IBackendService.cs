namespace SimpleL7Proxy;

public interface IBackendService
{
    void Start(CancellationToken cancellationToken);
    public List<BackendHost> GetActiveHosts();

    public int ActiveHostCount();

    public Task WaitForStartup(int timeout);
    public string HostStatus { get; }
    public void TrackStatus(int code);
    public bool CheckFailedStatus();
    public string OAuth2Token();
}
