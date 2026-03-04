namespace SimpleL7Proxy.Config;

/// <summary>
/// Describes a single configuration setting that changed during a refresh cycle.
/// </summary>
public readonly record struct ConfigChange
{
    /// <summary>Property name on <see cref="BackendOptions"/> (e.g. "LogConsole").</summary>
    public string PropertyName { get; init; }

    /// <summary>The App Configuration key path (e.g. "Logging:LogConsole").</summary>
    public string KeyPath { get; init; }

    /// <summary>Previous value (as string) before the change, or <c>null</c> if unknown.</summary>
    public string? OldValue { get; init; }

    /// <summary>New value (as string) after the change.</summary>
    public string? NewValue { get; init; }
}