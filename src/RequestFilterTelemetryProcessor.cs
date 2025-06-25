using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System;


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
        var type = item.GetType().Name;

        if (item is DependencyTelemetry request)
        {
            bool hasCustomProperties = request.Properties.ContainsKey("Ver") ||
                                        request.Properties.ContainsKey("Revision") ||
                                        request.Properties.ContainsKey("CustomTracked");
            if (request.Type == "Http" && !hasCustomProperties)
            {
                // Skip auto-generated HTTP requests that don't have our custom properties
                return;
            }
        }

        // Filter out request telemetry that doesn't have our custom properties
        if (item is RequestTelemetry requestTelemetry)
        {
            // Check if this request has our custom properties that we set in ProxyEvent
            bool hasCustomProperties = requestTelemetry.Properties.ContainsKey("Ver") ||
                                        requestTelemetry.Properties.ContainsKey("Revision") ||
                                        requestTelemetry.Properties.ContainsKey("CustomTracked");

            if (!hasCustomProperties)
            {
                // Skip auto-generated requests that don't have our custom properties
                return;
            }
            // foreach (var prop in requestTelemetry.Properties)
            // {
            //     Console.WriteLine($"{prop.Key}: {prop.Value}");
            // }
            // Console.WriteLine($"Request: {requestTelemetry.Name}");
            // Console.WriteLine("-------");
        }

        // Pass all other telemetry (including our custom requests) to the next processor
        _next.Process(item);
    }
}
