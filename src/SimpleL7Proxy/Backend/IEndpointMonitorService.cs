namespace SimpleL7Proxy.Backend;

/// <summary>
/// Interface for backend services supporting both DirectBackend and APIMBackend types.
/// </summary>
public interface IEndpointMonitorService
{
  List<BaseHostHealth> GetHosts();
  List<BaseHostHealth> GetActiveHosts();
  int ActiveHostCount();
  // BackendType BackendKind { get; }
  string HostStatus { get; }
  // void TrackStatus(int code, bool wasException);
  int EMSGetBackpressureDelay();
  // string OAuth2Token();
  Task WaitForStartupAsync();
  Task Stop();
  List<BaseHostHealth> GetSpecificPathHosts();
  List<BaseHostHealth> GetCatchAllHosts();
  PathRouteMatch? MatchRoute(string requestPath) => null;
}

public enum BackendType
{
  DirectBackend,
  APIMBackend
}
