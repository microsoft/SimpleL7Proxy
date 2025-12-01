namespace SimpleL7Proxy.Events;

public class EventHubConfig {
    public string? ConnectionString { get; }
    public string? EventHubName { get; }
    public string? EventHubNamespace { get; }
    public int StartupSeconds { get; } = 10;

    public EventHubConfig(string? connectionString, string? eventHubName, string? eventHubNamespace, int startupSeconds) {
        ConnectionString = connectionString;
        EventHubName = eventHubName;
        EventHubNamespace = eventHubNamespace;
        StartupSeconds = startupSeconds;

Console.WriteLine($"[CONFIG] EventHubConfig initialized. ConnectionString: {(string.IsNullOrEmpty(connectionString) ? "Not Set" : "Set")}, EventHubName: {eventHubName}, EventHubNamespace: {eventHubNamespace}");
    }
}