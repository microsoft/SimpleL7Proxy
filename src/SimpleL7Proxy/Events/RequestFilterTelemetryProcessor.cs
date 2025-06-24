using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System;


namespace SimpleL7Proxy.Events;
/// <summary>
/// Telemetry processor that filters out automatically generated request telemetry.
/// Preserves custom request telemetry created by ProxyEvent.
/// </summary>
public class RequestFilterTelemetryProcessor : ITelemetryProcessor
{
    private readonly ITelemetryProcessor _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestFilterTelemetryProcessor"/> class.
    /// </summary>
    /// <param name="next">The next processor in the chain.</param>
    public RequestFilterTelemetryProcessor(ITelemetryProcessor next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>
    /// Processes telemetry data to filter out auto-generated request telemetry.
    /// </summary>
    /// <param name="item">The telemetry item to process.</param>
    public void Process(ITelemetry item)
    {
        // Filter out automatically generated request telemetry
        // But keep our custom tracked requests from ProxyEvent
        if (item is RequestTelemetry requestTelemetry)
        {
            // If it doesn't have our custom properties, it's an auto-generated request
            // Your ProxyEvent adds Ver and Revision properties to custom requests
            if (!requestTelemetry.Properties.ContainsKey("Ver") &&
                !requestTelemetry.Properties.ContainsKey("Revision"))
            {
                // Don't process this item further
                return;
            }
        }

        // Pass the telemetry item to the next processor
        _next.Process(item);
    }
}
