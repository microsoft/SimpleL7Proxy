using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
    /// Diffs bare-keyed warm settings against live options. Does not mutate liveOptions.
    /// Host keys (Host*, Probe*, IP*) are returned separately in HostChanges.
    /// Caller must strip the "Warm:" prefix and exclude the sentinel before calling.
    /// </summary>
    public static (List<ConfigChange> Changes, Dictionary<string, object?> ParsedValues, Dictionary<string, string> HostChanges) DetectWarmChanges(
        BackendOptions liveOptions,
        Dictionary<string, string> warmSettings,
        ILogger? logger = null)
    {
        var changeList = new List<ConfigChange>();
        var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var hostChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultTarget = new BackendOptions();
        var env = new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in warmSettings)
        {
            var rawValue = kvp.Value;

            if (string.IsNullOrEmpty(rawValue))
                continue;

            var key = kvp.Key;
            if (key.StartsWith("Host") || key.StartsWith("Probe") || key.StartsWith("IP"))
            {
                hostChanges[key] = rawValue;
                continue;
            }

            if (!_warmDescriptorsByKeyPath.Value.TryGetValue(key, out var descriptor)
                && !_warmDescriptorsByConfigName.Value.TryGetValue(key, out descriptor))
                continue;

            var configName = descriptor.ConfigName;
            if (!TryGetFieldByConfigName(configName, out var field) || field == null)
                continue;

            var currentValue = field.GetValue(liveOptions);

            // Parse the raw value via ApplyFieldFromEnv on a throwaway target.
            // Single-entry dict keyed by configName so the lookup matches.
            env.Clear();
            env[configName] = rawValue;

            defaultTarget.ApplyFieldFromEnv(
                env,
                liveOptions,
                configName,
                field.Name);

            var newValue = field.GetValue(defaultTarget);

            if (DeepEquals(currentValue, newValue))
                continue;

            updates[configName] = newValue;
            changeList.Add(new ConfigChange
            {
                PropertyName = configName,
                KeyPath = descriptor.Attribute.KeyPath,
                RawOldValue = currentValue,
                RawNewValue = newValue
            });
        }

        return (changeList, updates, hostChanges);
    }

    /// <summary>Formats a value for logging, expanding collections to strings.</summary>
    private static string FormatValue(object? rawValue)
    {
        if (rawValue == null) return "";
        return rawValue switch
        {
            string s => s,
            int[] arr => string.Join(", ", arr),
            IEnumerable<string> list => string.Join(", ", list),
            IEnumerable<int> list => string.Join(", ", list),
            IDictionary<string, string> dict => string.Join(", ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}")),
            IDictionary<int, int> dict => string.Join(", ", dict.Select(kvp => $"{kvp.Key}:{kvp.Value}")),
            _ => rawValue.ToString() ?? ""
        };
    }

    /// <summary>Deep-compares two values, handling List, array, and Dictionary types.</summary>
    private static bool DeepEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.GetType() != b.GetType()) return false;

        return (a, b) switch
        {
            (int[] la, int[] lb) => la.SequenceEqual(lb),
            (IList<string> la, IList<string> lb) => la.SequenceEqual(lb),
            (IList<int> la, IList<int> lb) => la.SequenceEqual(lb),
            (IDictionary<string, string> da, IDictionary<string, string> db) =>
                da.Count == db.Count && da.All(kvp => db.TryGetValue(kvp.Key, out var v) && v == kvp.Value),
            (IDictionary<int, int> da, IDictionary<int, int> db) =>
                da.Count == db.Count && da.All(kvp => db.TryGetValue(kvp.Key, out var v) && v == kvp.Value),
            _ => Equals(a, b)
        };
    }

    /// <summary>Reflects over BackendOptions to discover all [ConfigOption] properties.</summary>
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
