using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;

namespace CompanionApp.Startup;

internal static class StartupEventHub
{
    internal static Dictionary<string, string> GetEventHubDefaults(
        EventHubMonitorOptions eventHubOptions)
    {
        return new Dictionary<string, string>
        {
            ["Cold:Logging:EventHub:Name"] =
                Environment.GetEnvironmentVariable("EVENTHUB_NAME") is { Length: > 0 } hubName
                && !string.IsNullOrWhiteSpace(hubName)
                    ? hubName
                    : eventHubOptions.EventHubName,
            ["Cold:Logging:EventHub:Namespace"] =
                Environment.GetEnvironmentVariable("EVENTHUB_NAMESPACE") is { Length: > 0 } hubNamespace
                && !string.IsNullOrWhiteSpace(hubNamespace)
                    ? hubNamespace
                    : eventHubOptions.EventHubNamespace,
            ["Cold:Logging:EventHub:ConnectionString"] =
                Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") is { Length: > 0 } hubConnectionString
                && !string.IsNullOrWhiteSpace(hubConnectionString)
                    ? hubConnectionString
                    : eventHubOptions.ConnectionString
        };
    }

    internal static bool HasEventHubDefaults(
        EventHubMonitorOptions eventHubOptions,
        Dictionary<string, string> eventHubDefaults)
    {
        return eventHubOptions.EventHubEnabled
            && !string.IsNullOrWhiteSpace(eventHubDefaults["Cold:Logging:EventHub:Name"])
            && (!string.IsNullOrWhiteSpace(eventHubDefaults["Cold:Logging:EventHub:Namespace"])
                || !string.IsNullOrWhiteSpace(
                    eventHubDefaults["Cold:Logging:EventHub:ConnectionString"]));
    }
}
