using Microsoft.ApplicationInsights;

namespace SimpleL7Proxy.Events;

public class AppInsightsEventClient : IEventClient
{
  private readonly TelemetryClient _telemetryClient;
  public AppInsightsEventClient(TelemetryClient telemetryClient)
  {
    _telemetryClient = telemetryClient;
  }
  public void SendData(string? value)
  {
    _telemetryClient.TrackEvent(value);
  }

  public void SendData(ProxyEvent proxyEvent)
  {
    if (string.IsNullOrEmpty(proxyEvent.Name))
    {
      if (proxyEvent.EventData.TryGetValue("Type", out var type))
      {
        proxyEvent.Name = type;
      }
      else
      {
        proxyEvent.Name = "ProxyEvent";
      }
    }
    _telemetryClient.TrackEvent(proxyEvent.Name, proxyEvent.EventData);
  }
}
