namespace SimpleL7Proxy.Config;

/// <summary>
/// Implement this interface to receive notifications when Azure App Configuration
/// settings change. Register via <see cref="ConfigChangeNotifier.Subscribe"/>.
/// </summary>
public interface IConfigChangeSubscriber
{
    /// <summary>
    /// Called when one or more warm configuration settings have changed.
    /// </summary>
    /// <param name="changes">The list of settings that changed in this refresh cycle.</param>
    /// <param name="backendOptions">The current <see cref="BackendOptions"/> instance (already updated).</param>
    /// <param name="cancellationToken">Cancellation token tied to the host lifetime.</param>
    Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        BackendOptions backendOptions,
        CancellationToken cancellationToken);
}
