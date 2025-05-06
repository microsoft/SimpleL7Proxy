namespace SimpleL7Proxy.Backend;

public interface IBackendService
{
  void Start();
  public List<BackendHostHealth> GetActiveHosts();
  public List<BackendHostHealth> GetHosts();

  public int ActiveHostCount();

  public Task WaitForStartup(int timeout);
  public Task Stop();
  public string HostStatus { get; }
  public void TrackStatus(int code, bool wasException);
  public bool CheckFailedStatus();
  public string OAuth2Token();
}
