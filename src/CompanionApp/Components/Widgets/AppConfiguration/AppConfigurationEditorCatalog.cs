namespace CompanionApp.Components.Widgets.AppConfiguration;

/// <summary>Presentation data for one configuration editor. Razor markup must not read this catalog.</summary>
public sealed record AppConfigurationEditorDescriptor(
    string Name,
    string Editor,
    string? ModalIcon,
    string? HelpSection,
    string SubtitleKind,
    string? StaticSubtitle = null,
    bool DedicatedBranch = true,
    bool OrdinalName = false);

/// <summary>
/// Editor identity for configuration sections. The page predicates may look up names here.
/// Header markup and editor branches must keep using their existing conditions.
/// </summary>
public static class AppConfigurationEditorCatalog
{
    /// <summary>No subtitle element is rendered.</summary>
    public const string SubtitleNone = "none";

    /// <summary>Subtitle is the selected setting count.</summary>
    public const string SubtitleSettingCount = "setting-count";

    /// <summary>Subtitle is the changed-setting count. The page compares this name with ordinal equality.</summary>
    public const string SubtitleChangedCount = "changed-count";

    /// <summary>Subtitle comes from HealthProbeEditor.IsEnabled.</summary>
    public const string SubtitleHealthProbeState = "health-probe-state";

    /// <summary>Subtitle comes from CircuitBreakerEditor.Summary.</summary>
    public const string SubtitleCircuitBreakerSummary = "circuit-breaker-summary";

    /// <summary>Subtitle comes from LoadBalancingEditor.Summary.</summary>
    public const string SubtitleLoadBalancingSummary = "load-balancing-summary";

    /// <summary>Subtitle is the selected header set, and only while one is selected.</summary>
    public const string SubtitleHeaderSet = "header-set";

    /// <summary>Subtitle is the static text on the descriptor.</summary>
    public const string SubtitleStatic = "static";

    /// <summary>Editor used by sections that have no dedicated branch.</summary>
    public const string GenericEditor = "GenericTable";

    /// <summary>Fallback for a section that has no descriptor. The page still renders the generic table.</summary>
    public static readonly AppConfigurationEditorDescriptor Generic = new(
        "General",
        GenericEditor,
        null,
        null,
        SubtitleSettingCount);

    private static readonly AppConfigurationEditorDescriptor[] KnownSections =
    [
        new("Local change review", "ChangeReview", null, null, SubtitleChangedCount, DedicatedBranch: true, OrdinalName: true),
        new("Hosts", "HostSectionEditor", null, "Hosts", SubtitleSettingCount),
        new("Proxy Runtime", "ProxyRuntimeEditor", "bi-gear-fill", "Server", SubtitleStatic,
            "Configure process settings, health reporting, and runtime behavior."),
        new("Backend Routing", "BackendRoutingEditor", "bi-diagram-3-fill", "LoadBalancing", SubtitleStatic,
            "Select, attempt, and communicate with backend targets."),
        new("HealthProbe", "HealthProbeEditor", null, "HealthProbe", SubtitleHealthProbeState),
        new("Backend Resilience", "BackendResilienceEditor", "bi-shield-fill-check", "CircuitBreaker", SubtitleStatic,
            "Define backend success criteria and protect the proxy from unhealthy dependencies."),
        new("CircuitBreaker", "CircuitBreakerEditor", null, "CircuitBreaker", SubtitleCircuitBreakerSummary),
        new("LoadBalancing", "LoadBalancingEditor", null, "LoadBalancing", SubtitleLoadBalancingSummary),
        new("Logging", "LoggingEditor", null, "Logging", SubtitleNone),
        new("Identity & Access", "IdentityAccessEditor", null, "Identity & Access", SubtitleSettingCount),
        new("Header Policy", "HeaderPolicyEditor", "bi-file-earmark-text-fill", "Request", SubtitleHeaderSet),
        new("Request", "RequestEditor", null, "Request", SubtitleSettingCount),
        new("Request Processing", "RequestProcessingEditor", "bi-arrow-left-right", "Request", SubtitleStatic,
            "Configure timeouts and request prioritization."),
        new("Response", "ResponseEditor", null, "Response", SubtitleSettingCount),
        new("Server", "ServerEditor", null, "Server", SubtitleSettingCount),
        new("Async", GenericEditor, null, "Async", SubtitleSettingCount, DedicatedBranch: false),
        new("Tokenomics", "TokenomicsEditor", null, null, SubtitleNone, DedicatedBranch: false),
    ];

    private static readonly Dictionary<string, AppConfigurationEditorDescriptor> SectionsByName =
        new(KnownSections.ToDictionary(section => section.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Named sections in editor-branch order, followed by sections that have no modal branch.</summary>
    public static IReadOnlyList<AppConfigurationEditorDescriptor> Sections { get; } = KnownSections;

    /// <summary>
    /// Returns a named editor descriptor. Matching is case-insensitive except for Local change review.
    /// Unknown names, including spaced HealthProbe, CircuitBreaker, and LoadBalancing variants, do not match.
    /// </summary>
    public static bool TryGet(string? sectionName, out AppConfigurationEditorDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(sectionName)
            || !SectionsByName.TryGetValue(sectionName, out descriptor!)
            || (descriptor.OrdinalName && !string.Equals(sectionName, descriptor.Name, StringComparison.Ordinal)))
        {
            descriptor = null!;
            return false;
        }

        return true;
    }

    /// <summary>Matches one predicate name. Null and unknown names return false.</summary>
    public static bool IsSection(string? sectionName, string name) =>
        TryGet(sectionName, out var descriptor) && string.Equals(descriptor.Name, name, StringComparison.Ordinal);
}
