using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class EventHubConfig {
    public string? ConnectionString { get; }
    public string? EventHubName { get; }
    public string? EventHubNamespace { get; }
    public int StartupSeconds { get; } = 10;
    public int MaxReconnectAttempts { get; } = 5;

    public EventHubConfig(BackendOptions options) {
        ConnectionString = options.EventHubConnectionString;
        EventHubName = options.EventHubName;
        EventHubNamespace = options.EventHubNamespace;
        StartupSeconds = options.EventHubStartupSeconds;
        MaxReconnectAttempts = options.EventHubMaxReconnectAttempts;

        // Valid config requires either (ConnectionString + EventHubName) or (EventHubNamespace + EventHubName)
        bool hasConnectionString = !string.IsNullOrEmpty(ConnectionString) && !string.IsNullOrEmpty(EventHubName);
        bool hasNamespace = !string.IsNullOrEmpty(EventHubNamespace) && !string.IsNullOrEmpty(EventHubName);

        if (!hasConnectionString && !hasNamespace)
        {
            Console.WriteLine("[CONFIG] EventHubConfig incomplete — need (EVENTHUB_CONNECTIONSTRING + EVENTHUB_NAME) or (EVENTHUB_NAMESPACE + EVENTHUB_NAME). EventHub logging will be disabled.");
            throw new InvalidOperationException("Incomplete EventHub configuration. Check logs for details.");
        }

        Console.WriteLine($"[CONFIG] EventHubConfig initialized. ConnectionString: {(string.IsNullOrEmpty(ConnectionString) ? "Not Set" : "Set")}, EventHubName: {EventHubName}, EventHubNamespace: {EventHubNamespace}, StartupSeconds: {StartupSeconds}");
    }
}