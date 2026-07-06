namespace SimpleL7Proxy.Plugin;

/// <summary>
/// A plugin for processing Azure configuration.
/// </summary>
public class AzureConfigPlugin : IConfigPlugin
{
    public string ContainerName { get; set; } = string.Empty;

    public string InstanceID { get; set; } = string.Empty;

    public string ConfigInstanceID { get; set; } = string.Empty;

    public Task<bool> ProcessAsync(CancellationToken cancellationToken = default)
    {
        ContainerName = Environment.GetEnvironmentVariable("CONTAINER_APP_NAME") ?? string.Empty;
        InstanceID = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? string.Empty;
        ConfigInstanceID = Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME") ?? string.Empty;

        return Task.FromResult(true);
    }
}