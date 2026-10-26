using CompanionApp.Components.Shared;

namespace CompanionApp.Components.Widgets.AppConfiguration;

/// <summary>Presentation data for one App Configuration section tile.</summary>
public sealed record AppConfigurationSectionDescriptor(
    string Name,
    string Icon,
    Func<IEnumerable<AppConfigurationScaffoldSetting>, string> Summarize,
    string SummaryClass = "config-box-meta",
    Func<string, string>? TooltipFallback = null)
{
    /// <summary>Tooltip used when no configured section tooltip exists.</summary>
    public string TooltipFor(string section) =>
        TooltipFallback?.Invoke(section) ?? $"Edit {section} settings.";
}

/// <summary>
/// Static presentation catalog for App Configuration section tiles.
/// Proxy Runtime, Backend Routing, and Identity and Access stay on dedicated widgets.
/// </summary>
public static class AppConfigurationSectionCatalog
{
    /// <summary>Icon used by the generic section tile.</summary>
    public const string GenericIcon = "bi-gear";

    /// <summary>Summary class used by tiles whose summary can be long.</summary>
    public const string BreakingSummaryClass = "config-box-meta text-break";

    /// <summary>Fallback descriptor. Sentinel and other unsectioned keys appear as General.</summary>
    public static readonly AppConfigurationSectionDescriptor Generic = new(
        "General",
        GenericIcon,
        SummarizeCount);

    private static readonly IReadOnlyDictionary<string, AppConfigurationSectionDescriptor> ThinSections =
        new Dictionary<string, AppConfigurationSectionDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["Request"] = new("Request", "bi-arrow-left-right", SummarizeCount),
            ["Server"] = new("Server", "bi-hdd-stack-fill", SummarizeCount),
            ["Logging"] = new("Logging", "bi-file-earmark-text-fill", LoggingEditor.Summary, BreakingSummaryClass),
            ["Header Policy"] = new(
                "Header Policy",
                "bi-file-earmark-text-fill",
                HeaderPolicyEditor.Summary,
                BreakingSummaryClass,
                static _ => "Edit header policy settings."),
            ["CircuitBreaker"] = new("CircuitBreaker", "bi-shield-fill", CircuitBreakerEditor.Summary),
            ["LoadBalancing"] = new("LoadBalancing", "bi-diagram-3-fill", LoadBalancingEditor.Summary),
            ["HealthProbe"] = new("HealthProbe", "bi-activity", SummarizeHealthProbe),
            ["Backend Resilience"] = new(
                "Backend Resilience",
                "bi-shield-fill-check",
                BackendResilienceEditor.Summary,
                TooltipFallback: static _ => "Edit backend resilience settings."),
            ["Request Processing"] = new(
                "Request Processing",
                "bi-cpu-fill",
                RequestProcessingEditor.Summary,
                BreakingSummaryClass,
                static _ => "Edit request processing settings."),
            ["Hosts"] = new("Hosts", "bi-database-fill", SummarizeHosts),
            ["Response"] = new("Response", "bi-arrow-return-left", SummarizeResponse),
            ["Async"] = new("Async", "bi-gear-fill", SummarizeAsync),
        };

    /// <summary>
    /// Returns the generic descriptor for a section that has no dedicated tile widget.
    /// The returned name preserves the caller's casing.
    /// </summary>
    public static AppConfigurationSectionDescriptor ForGenericSection(string sectionName) =>
        Generic with { Name = sectionName };

    /// <summary>
    /// Returns a thin tile descriptor. The lookup uses the same names as the former section switch.
    /// </summary>
    public static bool TryGetThinSection(string sectionName, out AppConfigurationSectionDescriptor descriptor) =>
        ThinSections.TryGetValue(sectionName, out descriptor!);

    /// <summary>Matches the generic tile summary: a setting count, with no pluralization.</summary>
    public static string SummarizeCount(IEnumerable<AppConfigurationScaffoldSetting> settings) =>
        $"{settings.Count()} settings";

    /// <summary>Matches the Hosts tile, including the singular label.</summary>
    public static string SummarizeHosts(IEnumerable<AppConfigurationScaffoldSetting> settings)
    {
        var count = settings.Count();
        return $"{count} {(count == 1 ? "host" : "hosts")}";
    }

    /// <summary>Matches the Health Probe tile. An empty section remains Disabled.</summary>
    public static string SummarizeHealthProbe(IEnumerable<AppConfigurationScaffoldSetting> settings) =>
        settings.FirstOrDefault() is { } probe && HealthProbeEditor.IsEnabled(probe) ? "Enabled" : "Disabled";

    /// <summary>Matches the Async tile, including the experimental prefix.</summary>
    public static string SummarizeAsync(IEnumerable<AppConfigurationScaffoldSetting> settings) =>
        "Experimental · " + (settings.Any(setting =>
            string.Equals(setting.Name, "Enabled", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(setting.DraftValue, out var enabled)
            && enabled) ? "Enabled" : "Disabled");

    /// <summary>Matches the Response tile's non-default count.</summary>
    public static string SummarizeResponse(IEnumerable<AppConfigurationScaffoldSetting> settings)
    {
        var defaultStatusCodes = new HashSet<int> { 200, 202, 401, 403, 404, 408, 410, 412, 417, 400 };
        var nonDefaultCount = settings.Count(setting =>
        {
            var values = setting.DraftValue.Trim().Trim('[', ']')
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().Trim('"'))
                .Where(value => value.Length > 0)
                .ToArray();
            return setting.Name.Equals("AcceptableStatusCodes", StringComparison.OrdinalIgnoreCase)
                ? !defaultStatusCodes.SetEquals(values.Select(value => int.TryParse(value, out var statusCode) ? statusCode : -1))
                : setting.Name.Equals("StripResponseHeaders", StringComparison.OrdinalIgnoreCase) && values.Length > 0;
        });
        return nonDefaultCount == 0
            ? "Defaults"
            : $"{nonDefaultCount} non-default {(nonDefaultCount == 1 ? "setting" : "settings")}";
    }
}
