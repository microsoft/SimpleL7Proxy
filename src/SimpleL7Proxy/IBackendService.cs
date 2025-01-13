public interface IBackendService
{
    void Start(CancellationToken cancellationToken);
    public List<BackendHost> GetActiveHosts();

    public int ActiveHostCount();

    public Task waitForStartup(int timeout);
    public string HostStatus();
    public void TrackStatus(int code);
    public bool CheckFailedStatus();
    public string OAuth2Token();
}
