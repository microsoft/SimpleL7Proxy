using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Controls how a <see cref="BackendOptions"/> property is published
/// and reloaded.
/// </summary>
public enum ConfigMode
{
    /// <summary>
    /// Published to App Configuration.  Changes are hot-reloaded
    /// (typically within 30 seconds) — no restart required.
    /// </summary>
    Warm,

    /// <summary>
    /// Published to App Configuration.  Changes require an
    /// application restart to take effect.
    /// </summary>
    Cold,

    /// <summary>
    /// Not published.  Used for runtime-derived, composite, or
    /// sensitive properties that should never appear in App Configuration.
    /// </summary>
    Hidden
}

/// <summary>
/// Marks a <see cref="BackendOptions"/> property as a managed config option.
/// <para>
/// <b>Warm</b> (default): published to App Configuration and hot-reloaded.<br/>
/// <b>Cold</b>: published to App Configuration but requires restart.<br/>
/// <b>Hidden</b>: not published — for runtime or composite values.
/// </para>
/// <para>
/// <c>keyPath</c> defines the section path under a prefix that matches
/// the <see cref="ConfigMode"/>: <c>Warm:</c> or <c>Cold:</c>
/// (e.g. <c>"Logging:LogConsole"</c> → <c>Warm:Logging:LogConsole</c>,
///       <c>"Server:Workers"</c> → <c>Cold:Server:Workers</c>).
/// </para>
/// <para>
/// Use <c>ConfigName</c> when the source env var differs from the property name:
/// <code>[ConfigOption("Metadata:ContainerApp", ConfigName = "CONTAINER_APP_NAME")]</code>
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConfigOptionAttribute : Attribute
{
    public ConfigOptionAttribute(string keyPath)
    {
        KeyPath = keyPath;
    }

    /// <summary>Key path under the mode section, e.g. "Logging:LogConsole" → Warm:Logging:LogConsole or Cold:Server:Workers.</summary>
    public string KeyPath { get; }

    /// <summary>
    /// Source environment variable / config name used by deployment tooling.
    /// Defaults to the property name when not specified.
    /// </summary>
    public string? ConfigName { get; set; }

    /// <summary>
    /// How this property is published and reloaded.
    /// Default: <see cref="ConfigMode.Warm"/>.
    /// </summary>
    public ConfigMode Mode { get; set; } = ConfigMode.Warm;
}

/// <summary>
/// Marks a <see cref="BackendOptions"/> property whose default value is
/// parsed from a composite configuration string (e.g. AsyncBlobStorageConfig,
/// AsyncSBConfig) rather than read from a single environment variable.
/// These properties are typically marked <see cref="ConfigMode.Hidden"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ParsedConfigAttribute : Attribute
{
    public ParsedConfigAttribute(string sourceConfig)
    {
        SourceConfig = sourceConfig;
    }

    /// <summary>Name of the composite config string this property is parsed from.</summary>
    public string SourceConfig { get; }
}

public sealed class ConfigOptionDescriptor
{
    public required PropertyInfo Property { get; init; }
    public required ConfigOptionAttribute Attribute { get; init; }

    /// <summary>
    /// Resolved config name: explicit ConfigName if set, otherwise the property name.
    /// </summary>
    public string ConfigName => Attribute.ConfigName ?? Property.Name;

    /// <summary>The reload mode for this config option.</summary>
    public ConfigMode Mode => Attribute.Mode;

    /// <summary>Whether this option is published to App Configuration by deploy.sh.</summary>
    public bool IsPublished => Mode != ConfigMode.Hidden;
}

/// <summary>
/// Discovers and applies config options dynamically based on
/// <see cref="ConfigOptionAttribute"/> decorations on
/// <see cref="BackendOptions"/> properties.
/// </summary>
public static class ConfigOptions
{
    private static readonly Lazy<IReadOnlyList<ConfigOptionDescriptor>> _descriptors = new(DiscoverDescriptors);
    private static readonly Lazy<IReadOnlyList<ConfigOptionDescriptor>> _warmDescriptors =
        new(() => Descriptors.Where(d => d.Mode == ConfigMode.Warm).ToList());
    private static readonly Lazy<IReadOnlyDictionary<string, ConfigOptionDescriptor>> _warmDescriptorsByConfigName =
        new(() => _warmDescriptors.Value.ToDictionary(d => d.ConfigName, d => d, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyDictionary<string, ConfigOptionDescriptor>> _warmDescriptorsByKeyPath =
        new(() => _warmDescriptors.Value.ToDictionary(d => d.Attribute.KeyPath, d => d, StringComparer.OrdinalIgnoreCase));
    private static readonly Lazy<IReadOnlyDictionary<string, PropertyInfo>> _fieldsByConfigName =
        new(() => Descriptors.ToDictionary(d => d.ConfigName, d => d.Property, StringComparer.OrdinalIgnoreCase));

    /// <summary>All discovered config option descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> Descriptors => _descriptors.Value;

    /// <summary>Returns all discovered config option descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetDescriptors() => Descriptors;

    /// <summary>Returns only warm (hot-reloadable) descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetWarmDescriptors() =>
        _warmDescriptors.Value;

    /// <summary>
    /// Returns a reverse map from configuration name to <see cref="BackendOptions"/> field/property.
    /// Computed once and cached for the process lifetime.
    /// </summary>
    public static IReadOnlyDictionary<string, PropertyInfo> GetFieldsByConfigName() =>
        _fieldsByConfigName.Value;

    /// <summary>
    /// Tries to resolve a <see cref="BackendOptions"/> field/property by configuration name.
    /// </summary>
    public static bool TryGetFieldByConfigName(string configName, out PropertyInfo? field) =>
        _fieldsByConfigName.Value.TryGetValue(configName, out field);

    /// <summary>Returns only publishable (Warm + Cold) descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetPublishableDescriptors() =>
        Descriptors.Where(d => d.IsPublished).ToList();

    /// <summary>
    /// Placeholder value written by deploy.sh when no env value or C# default
    /// exists. Treated as "use the built-in code default" — the property is
    /// left unchanged.
    /// </summary>
    public const string DefaultPlaceholder = "-";

    // /// <summary>
    // /// Applies warm-mode config values from the given configuration section
    // /// to the target <see cref="BackendOptions"/> instance.
    // /// Only properties with <see cref="ConfigMode.Warm"/> are applied.
    // /// Values equal to <see cref="DefaultPlaceholder"/> are ignored,
    // /// leaving the built-in code default in place.
    // /// </summary>
    // public static List<ConfigChange> ApplyWarmTo(BackendOptions target, IConfiguration warmSection, ILogger? logger = null)
    // {
    //     var (changes, parsedValues) = DetectWarmChanges(target, warmSection, logger);

    //     foreach (var change in changes)
    //     {
    //         if (!parsedValues.TryGetValue(change.PropertyName, out var newValue))
    //             continue;

    //         if (!_warmDescriptorsByConfigName.Value.TryGetValue(change.PropertyName, out var descriptor))
    //             continue;

    //         descriptor.Property.SetValue(target, newValue);
    //         logger?.LogInformation("[CONFIG] Updated {Property}: {Old} → {New}",
    //             descriptor.ConfigName, change.OldValue, change.NewValue);
    //     }

    //     return changes;
    // }

    /// <summary>
    /// Iterates over a <c>Warm:</c>-prefixed configuration snapshot and detects
    /// values that differ from <paramref name="liveOptions"/>.
    /// <para>
    /// Each snapshot key is resolved to a <see cref="BackendOptions"/> property
    /// via the key-path or config-name descriptor maps.  Host-family keys
    /// (<c>Host*</c>, <c>Probe_path*</c>, <c>IP*</c>) are collected separately
    /// in <c>HostChanges</c> since they are not backed by descriptors.
    /// </para>
    /// <para>Does not mutate <paramref name="liveOptions"/>.</para>
    /// </summary>
    /// <param name="liveOptions">The current in-memory <see cref="BackendOptions"/>.</param>
    /// <param name="snapshot">
    /// Flat dictionary captured from the <c>Warm:</c> configuration section.
    /// Keys are prefixed (e.g. <c>Warm:Logging:LogConsole</c>, <c>Warm:Host1</c>).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <returns>
    /// A tuple of descriptor-backed changes with their parsed values, plus a
    /// dictionary of host-family key changes keyed by bare name (e.g. <c>Host1</c>).
    /// </returns>
    public static (List<ConfigChange> Changes, Dictionary<string, object?> ParsedValues, Dictionary<string, string> HostChanges) DetectWarmChanges(
        BackendOptions liveOptions,
        Dictionary<string, string> snapshot,
        ILogger? logger = null)
    {
        var changes = new List<ConfigChange>();
        var parsedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var hostChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultTarget = new BackendOptions();
        var env = new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in snapshot)
        {
            var snapshotKey = kvp.Key["Warm:".Length..];
            var rawValue = kvp.Value;

            if (string.IsNullOrEmpty(rawValue))
                continue;

            if (snapshotKey.StartsWith("Host") || snapshotKey.StartsWith("Probe") || snapshotKey.StartsWith("IP"))
            {
                // skip Host/Probe/IP entries which are used for dynamic host discovery and not mapped to BackendOptions properties
                hostChanges[snapshotKey] = rawValue;
                continue;
            }

            if (!_warmDescriptorsByKeyPath.Value.TryGetValue(snapshotKey, out var descriptor)
                && !_warmDescriptorsByConfigName.Value.TryGetValue(snapshotKey, out descriptor))
                continue;

            var configName = descriptor.ConfigName;
            if (!TryGetFieldByConfigName(configName, out var field) || field == null)
                continue;


            var currentValue = field.GetValue(liveOptions);

            object? newValue;
            if (rawValue == DefaultPlaceholder)
            {
                newValue = field.GetValue(defaultTarget);
            }
            else
            {
                env.Clear();
                env[configName] = rawValue;

                ConfigParser.ApplyFieldFromEnv(
                    env,
                    defaultTarget,
                    liveOptions,
                    configName,
                    field.Name);

                newValue = field.GetValue(defaultTarget);
            }

            if (Equals(currentValue, newValue))
                continue;

            parsedValues[configName] = newValue;
            changes.Add(new ConfigChange
            {
                PropertyName = configName,
                KeyPath = descriptor.Attribute.KeyPath,
                RawOldValue = currentValue,
                RawNewValue = newValue
            });
        }

        return (changes, parsedValues, hostChanges);
    }

    private static IReadOnlyList<ConfigOptionDescriptor> DiscoverDescriptors()
    {
        return typeof(BackendOptions)
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
