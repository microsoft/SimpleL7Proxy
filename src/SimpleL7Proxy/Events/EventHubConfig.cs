namespace SimpleL7Proxy.Events;

public class EventHubConfig {
    public string? ConnectionString { get; }
    public string? EventHubName { get; }
    
    public EventHubConfig(string? connectionString, string? eventHubName) {
        ConnectionString = connectionString;
        EventHubName = eventHubName;
    }
}