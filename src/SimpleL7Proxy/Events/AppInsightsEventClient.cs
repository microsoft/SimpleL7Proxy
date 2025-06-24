using Microsoft.ApplicationInsights;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

public class AppInsightsEventClient(TelemetryClient telemetryClient)
  : IEventClient, IHostedService
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
    string Name = proxyEvent.TryGetValue("Type", out var type)
        ? type : "ProxyEvent";

    telemetryClient.TrackEvent(Name, proxyEvent);
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    // App Insights doesn't need initialization
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    // Flush any remaining telemetry
    telemetryClient.Flush();
    return Task.CompletedTask;
  }

  // public void SendData(Dictionary<string, string> data)
  // {
  //   telemetryClient.TrackEvent("ProxyEvent", data);
  // }


  //   public void SendData(ConcurrentDictionary<string, string> eventData, string? name = "ProxyEvent")
  // {
  //   telemetryClient.TrackEvent(name, eventData);
  // }
}
