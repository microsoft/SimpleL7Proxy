using Microsoft.ApplicationInsights;

namespace SimpleL7Proxy.Events;

public class AppInsightsEventClient(TelemetryClient telemetryClient)
  : IEventClient
{

public Task StartTimer()
  {
    // No timer needed for App Insights
    return Task.CompletedTask;
  }
  public void StopTimer() { }
  public int Count => 0;
  public void SendData(string? value) => telemetryClient.TrackEvent(value);

  public void SendData(ProxyEvent proxyEvent)
  {
    if (string.IsNullOrEmpty(proxyEvent.Name))
    {
      proxyEvent.Name = proxyEvent.EventData.TryGetValue("Type", out var type)
        ? type : "ProxyEvent";
    }
    telemetryClient.TrackEvent(proxyEvent.Name, proxyEvent.EventData);
  }

  public void SendData(Dictionary<string, string> data)
  {
    telemetryClient.TrackEvent("ProxyEvent", data);
  }
}
