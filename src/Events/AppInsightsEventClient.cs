using Microsoft.ApplicationInsights;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    // TODO - check if there's a Type header
    if (string.IsNullOrEmpty(proxyEvent.Name))
    {
      proxyEvent.Name = "ProxyEvent";
    }
    _telemetryClient.TrackEvent(proxyEvent.Name, proxyEvent.EventData);
  }
}
