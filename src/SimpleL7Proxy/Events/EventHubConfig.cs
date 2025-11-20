namespace SimpleL7Proxy.Events;

public class EventHubConfig {
    public string? ConnectionString { get; }
    public string? EventHubName { get; }
    public string? EventHubNamespace { get; }

    public EventHubConfig(string? connectionString, string? eventHubName, string? eventHubNamespace) {
        ConnectionString = connectionString;
        EventHubName = eventHubName;
        EventHubNamespace = eventHubNamespace;

Console.WriteLine($"[CONFIG] EventHubConfig initialized. ConnectionString: {(string.IsNullOrEmpty(connectionString) ? "Not Set" : "Set")}, EventHubName: {eventHubName}, EventHubNamespace: {eventHubNamespace}");
    }
}