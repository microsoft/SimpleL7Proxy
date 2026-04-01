using System.Reflection;

namespace SimpleL7Proxy.Config;

/// <summary>How a BackendOptions property is published and reloaded.</summary>
public enum ConfigMode
{
    /// <summary>Hot-reloaded from App Configuration (~30 s). No restart needed.</summary>
    Warm,

    /// <summary>Published to App Configuration but requires a restart.</summary>
    Cold,

    /// <summary>Runtime-derived or sensitive. Never published to App Configuration.</summary>
    Hidden
}

/// <summary>
/// Marks a BackendOptions property as a managed config option.
/// <c>keyPath</c> is the section path under the mode prefix
/// (e.g. "Logging:LogConsole" → Warm:Logging:LogConsole).
/// Set <c>ConfigName</c> when the env var differs from the property name.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConfigOptionAttribute : Attribute
{
    public ConfigOptionAttribute(string keyPath)
    {
        KeyPath = keyPath;
    }

    /// <summary>Section path under the Warm:/Cold: prefix.</summary>
    public string KeyPath { get; }

    /// <summary>Env var / config name override. Defaults to the property name.</summary>
    public string? ConfigName { get; set; }

    /// <summary>Reload mode. Default: Warm.</summary>
    public ConfigMode Mode { get; set; } = ConfigMode.Warm;
}

/// <summary>
/// Marks a property whose default is parsed from a composite config string
/// (e.g. AsyncSBConfig). Typically Hidden.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ParsedConfigAttribute : Attribute
{
    public ParsedConfigAttribute(string sourceConfig)
    {
        SourceConfig = sourceConfig;
    }

    /// <summary>Composite config string this property is parsed from.</summary>
    public string SourceConfig { get; }
}

/// <summary>Metadata for a single [ConfigOption]-decorated BackendOptions property.</summary>
public sealed class ConfigOptionDescriptor
{
    public required PropertyInfo Property { get; init; }
    public required ConfigOptionAttribute Attribute { get; init; }

    /// <summary>Resolved config name: explicit ConfigName ?? property name.</summary>
    public string ConfigName => Attribute.ConfigName ?? Property.Name;

    /// <summary>Reload mode for this option.</summary>
    public ConfigMode Mode => Attribute.Mode;

    /// <summary>True for Warm and Cold (published to App Configuration).</summary>
    public bool IsPublished => Mode != ConfigMode.Hidden;
}

/// <summary>
/// Discovers [ConfigOption] descriptors on BackendOptions and provides
/// warm change detection for hot-reload.
/// </summary>
public static class ConfigMetadata
{
    private static readonly Lazy<IReadOnlyList<ConfigOptionDescriptor>> _descriptors = new(DiscoverDescriptors);
    private static readonly Lazy<IReadOnlyList<ConfigOptionDescriptor>> _warmDescriptors =
        new(() => Descriptors.Where(d => d.Mode == ConfigMode.Warm).ToList());
    private static readonly Lazy<IReadOnlyDictionary<string, ConfigOptionDescriptor>> _warmDescriptorsByConfigName =
        new(() => _warmDescriptors.Value.ToDictionary(d => d.ConfigName, d => d, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyDictionary<string, ConfigOptionDescriptor>> _warmDescriptorsByKeyPath =
        new(() => _warmDescriptors.Value.ToDictionary(d => d.Attribute.KeyPath, d => d, StringComparer.OrdinalIgnoreCase));

    /// <summary>Warm descriptors keyed by config name.</summary>
    public static IReadOnlyDictionary<string, ConfigOptionDescriptor> WarmDescriptorsByConfigName =>
        _warmDescriptorsByConfigName.Value;

    /// <summary>Warm descriptors keyed by key path.</summary>
    public static IReadOnlyDictionary<string, ConfigOptionDescriptor> WarmDescriptorsByKeyPath =>
        _warmDescriptorsByKeyPath.Value;
    private static readonly Lazy<IReadOnlyDictionary<string, PropertyInfo>> _fieldsByConfigName =
        new(() => Descriptors.ToDictionary(d => d.ConfigName, d => d.Property, StringComparer.OrdinalIgnoreCase));

    /// <summary>All discovered descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> Descriptors => _descriptors.Value;

    /// <summary>All discovered descriptors (same as Descriptors).</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetDescriptors() => Descriptors;

    /// <summary>Warm (hot-reloadable) descriptors only.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetWarmDescriptors() =>
        _warmDescriptors.Value;

    /// <summary>Config name → PropertyInfo map. Cached for process lifetime.</summary>
    public static IReadOnlyDictionary<string, PropertyInfo> GetFieldsByConfigName() =>
        _fieldsByConfigName.Value;

    /// <summary>Looks up a PropertyInfo by config name.</summary>
    public static bool TryGetFieldByConfigName(string configName, out PropertyInfo? field) =>
        _fieldsByConfigName.Value.TryGetValue(configName, out field);

    /// <summary>Warm + Cold descriptors (everything published to App Configuration).</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetPublishableDescriptors() =>
        Descriptors.Where(d => d.IsPublished).ToList();

    /// <summary>
    /// Placeholder value written by deploy.sh when no value exists.
    /// Treated as "use the code default" — the property is left unchanged.
    /// </summary>
    public const string DefaultPlaceholder = "-";

    /// <summary>Reflects over BackendOptions to discover all [ConfigOption] properties.</summary>
    private static IReadOnlyList<ConfigOptionDescriptor> DiscoverDescriptors()
    {
        return typeof(ProxyConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => prop.CanRead && prop.CanWrite)
            .Select(prop => new
            {
                Property = prop,
                Attribute = prop.GetCustomAttribute<ConfigOptionAttribute>()
            })
            .Where(x => x.Attribute != null)
            .Select(x => new ConfigOptionDescriptor
            {
                Property = x.Property,
                Attribute = x.Attribute!
            })
            .ToList();
    }
}
