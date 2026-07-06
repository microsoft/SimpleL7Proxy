namespace SimpleL7Proxy.Plugin;

public interface IConfigPlugin
{
    string ContainerName { get; }
    string InstanceID { get; }
    string ConfigInstanceID { get; }
    Task<bool> ProcessAsync(CancellationToken cancellationToken = default);
}