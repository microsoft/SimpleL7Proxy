using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace MetricsServer;

/// <summary>
/// Stamps every telemetry item with the metrics server role name and version so that data from
/// multiple replicas can be separated in Application Insights.
/// </summary>
public sealed class MetricsTelemetryInitializer : ITelemetryInitializer
{
    private static readonly string s_roleInstance = Environment.MachineName;

    /// <summary>
    /// Applies the cloud role name, role instance, and component version to a telemetry item.
    /// </summary>
    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is null)
        {
            return;
        }

        telemetry.Context.Cloud.RoleName = Constants.Server;
        telemetry.Context.Cloud.RoleInstance = s_roleInstance;
        telemetry.Context.Component.Version = Constants.VERSION;
    }
}
