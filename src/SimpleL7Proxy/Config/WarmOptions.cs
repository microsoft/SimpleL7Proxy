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
/// <c>keyPath</c> defines the section path under the <c>Warm:</c> prefix
/// (e.g. <c>"Logging:LogConsole"</c> → <c>Warm:Logging:LogConsole</c>).
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

    /// <summary>Key path under the Warm section (e.g. "Logging:LogConsole").</summary>
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

    /// <summary>All discovered config option descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> Descriptors => _descriptors.Value;

    /// <summary>Returns all discovered config option descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetDescriptors() => Descriptors;

    /// <summary>Returns only warm (hot-reloadable) descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetWarmDescriptors() =>
        Descriptors.Where(d => d.Mode == ConfigMode.Warm).ToList();

    /// <summary>Returns only publishable (Warm + Cold) descriptors.</summary>
    public static IReadOnlyList<ConfigOptionDescriptor> GetPublishableDescriptors() =>
        Descriptors.Where(d => d.IsPublished).ToList();

    /// <summary>
    /// Applies warm-mode config values from the given configuration section
    /// to the target <see cref="BackendOptions"/> instance.
    /// Only properties with <see cref="ConfigMode.Warm"/> are applied.
    /// </summary>
    public static int ApplyWarmTo(BackendOptions target, IConfiguration warmSection, ILogger? logger = null)
    {
        var applied = 0;

        foreach (var descriptor in Descriptors)
        {
            if (descriptor.Mode != ConfigMode.Warm)
                continue;

            var section = warmSection.GetSection(descriptor.Attribute.KeyPath);
            if (!section.Exists())
                continue;

            var converted = section.Get(descriptor.Property.PropertyType);
            if (converted == null)
                continue;

            descriptor.Property.SetValue(target, converted);
            applied++;
        }

        logger?.LogDebug("[CONFIG] Applied {Count} warm config options", applied);
        return applied;
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
