namespace SimpleL7Proxy.Config;

/// <summary>
/// Extension methods for <see cref="BackendOptions"/> that provide a cleaner
/// calling syntax for configuration parsing operations.
/// </summary>
public static class BackendOptionsExtensions
{
    /// <summary>
    /// Applies a single configuration field from the environment dictionary to this
    /// <see cref="BackendOptions"/> instance, falling back to the corresponding
    /// default value when the environment variable is absent or set to the
    /// default placeholder.
    /// </summary>
    /// <param name="target">The <see cref="BackendOptions"/> instance to update.</param>
    /// <param name="env">Dictionary of environment/configuration key-value pairs.</param>
    /// <param name="defaults">A default <see cref="BackendOptions"/> instance providing fallback values.</param>
    /// <param name="envVar">The environment variable (dictionary key) to look up.</param>
    /// <param name="property">The name of the <see cref="BackendOptions"/> property to set.</param>
    public static void ApplyFieldFromEnv(this BackendOptions target, Dictionary<string, string> env, BackendOptions defaults, string envVar, string property)
    {
        ConfigParser.ApplyFieldFromEnv(env, target, defaults, envVar, property);
    }
}
