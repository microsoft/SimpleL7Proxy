using Microsoft.ApplicationInsights;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace SimpleL7Proxy.Events;

public class AppInsightsEventClient : IEventClient, IHostedService
{
  private readonly TelemetryClient? _telemetryClient;
  private readonly CompositeEventClient _composite;

  public AppInsightsEventClient(TelemetryClient? telemetryClient, CompositeEventClient composite)
  {
    _telemetryClient = telemetryClient;
    _composite = composite ?? throw new ArgumentNullException(nameof(composite));
  }

  public Task StopTimerAsync() => Task.CompletedTask;
  public int Count => 0;
  public string ClientType => _telemetryClient is not null ? "AppInsights" : "AppInsights (Disabled)";
  public void SendData(string? value) => _telemetryClient?.TrackEvent(value);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    if (_telemetryClient is not null)
      _composite.Add(this);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    // Flush any remaining telemetry
    _telemetryClient?.Flush();
    return Task.CompletedTask;
  }
}
