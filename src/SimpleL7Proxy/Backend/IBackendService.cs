namespace SimpleL7Proxy.Backend;

public interface IBackendService
{
  void Start();
  public List<BackendHostHealth> GetActiveHosts();

  public int ActiveHostCount();

  public Task WaitForStartup(int timeout);
  public string HostStatus { get; }
  public void TrackStatus(int code);
  public bool CheckFailedStatus();
  public string OAuth2Token();
}
