namespace SimpleL7Proxy.Config;

/// <summary>
/// Describes a single configuration setting that changed during a refresh cycle.
/// String representations of <see cref="OldValue"/> and <see cref="NewValue"/>
/// are computed lazily on first access to avoid unnecessary allocations.
/// </summary>
public readonly record struct ConfigChange
{
    private readonly object? _oldValue;
    private readonly object? _newValue;

    /// <summary>Property name on <see cref="ProxyConfig"/> (e.g. "LogConsole").</summary>
    public string PropertyName { get; init; }

    /// <summary>The App Configuration key path (e.g. "Logging:LogConsole").</summary>
    public string KeyPath { get; init; }

    /// <summary>
    /// Raw previous value before the change, or <c>null</c> if unknown.
    /// </summary>
    public object? RawOldValue { get => _oldValue; init => _oldValue = value; }

    /// <summary>
    /// Raw new value after the change.
    /// </summary>
    public object? RawNewValue { get => _newValue; init => _newValue = value; }

    /// <summary>Previous value (as string) before the change. Computed lazily from <see cref="RawOldValue"/>.</summary>
    public string? OldValue => _oldValue?.ToString();

    /// <summary>New value (as string) after the change. Computed lazily from <see cref="RawNewValue"/>.</summary>
    public string? NewValue => _newValue?.ToString();
}