
/// <summary>
/// A no-op implementation of IEventHubClient used when Event Hub is disabled.
/// </summary>
public class NullEventHubClient : IEventHubClient
{
    public int Count => 0;

    public bool isHealthy => true;

    public bool IsRunning { get; set; } = false;

    public int GetEntryCount() => 0;

    public void SendData(string? value)
    {
        // No-op
    }

    public void SendData(ProxyEvent eventData)
    {
        // No-op
    }

    public Task StartTimer()
    {
        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        // No-op
    }

    public Task StopTimer()
    {
        return Task.CompletedTask;
    }
}
