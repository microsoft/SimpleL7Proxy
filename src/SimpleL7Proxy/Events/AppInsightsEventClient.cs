using Microsoft.ApplicationInsights;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

public class AppInsightsEventClient(TelemetryClient telemetryClient)
  : IEventClient, IHostedService
{
  public Task StopTimerAsync() => Task.CompletedTask;
  public int Count => 0;
  public string ClientType => "AppInsights";
  public void SendData(string? value) => telemetryClient.TrackEvent(value);

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
}
