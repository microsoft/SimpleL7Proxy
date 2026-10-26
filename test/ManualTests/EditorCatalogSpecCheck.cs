#:project ../../src/CompanionApp/CompanionApp.csproj

using CompanionApp.Components.Widgets.AppConfiguration;

var root = FindRepoRoot();
var pagePath = Path.Combine(root, "src/CompanionApp/Components/Pages/AdminConfigurationPage.razor");
var page = File.ReadAllText(pagePath);
var failures = new List<string>();

Check(page.Contains("AppConfigurationEditorCatalog", StringComparison.Ordinal), "page contains the editor catalog");
var markup = page.Split("@code", 2)[0];
Check(!markup.Contains("AppConfigurationEditorCatalog", StringComparison.Ordinal), "Razor markup does not call the editor catalog");

var header = Slice(page, "<HeaderContent>", "</HeaderContent>");
var editor = Slice(page, "<EditorContent>", "</EditorContent>");
RequireOrder(header, "header", [
    "IsHeaderPolicySection",
    "bi-file-earmark-text-fill",
    "IsRequestProcessingSection",
    "bi-arrow-left-right",
    "IsBackendResilienceSection",
    "bi-shield-fill-check",
    "IsProxyRuntimeSection",
    "bi-gear-fill",
    "IsBackendRoutingSection",
    "bi-diagram-3-fill",
    "SelectedSection != \"Local change review\"",
    "IsRequestProcessingSection || IsHeaderPolicySection",
    "? \"Request\"",
    "IsBackendResilienceSection",
    "? \"CircuitBreaker\"",
    "IsProxyRuntimeSection",
    "? \"Server\"",
    "IsBackendRoutingSection",
    "? \"LoadBalancing\"",
    ": SelectedSection",
    "SelectedSection == \"Local change review\"",
    "setting changed",
    "IsHealthProbeSection",
    "HealthProbeEditor.IsEnabled",
    "IsCircuitBreakerSection",
    "CircuitBreakerEditor.Summary",
    "IsLoadBalancingSection",
    "LoadBalancingEditor.Summary",
    "IsHeaderPolicySection",
    "HeaderPolicyEditor.HeaderSetTitle",
    "IsRequestProcessingSection",
    "Configure timeouts and request prioritization.",
    "IsBackendResilienceSection",
    "Define backend success criteria and protect the proxy from unhealthy dependencies.",
    "IsProxyRuntimeSection",
    "Configure process settings, health reporting, and runtime behavior.",
    "IsBackendRoutingSection",
    "Select, attempt, and communicate with backend targets.",
    "!string.Equals(SelectedSection, \"Logging\", StringComparison.OrdinalIgnoreCase)",
    "@SelectedSettings.Count settings",
]);

RequireOrder(editor, "editor", [
    "SelectedSection == \"Local change review\"",
    "else if (IsHostSection)",
    "<HostSectionEditor",
    "else if (IsProxyRuntimeSection)",
    "<ProxyRuntimeEditor",
    "else if (IsBackendRoutingSection)",
    "<BackendRoutingEditor",
    "else if (IsHealthProbeSection)",
    "HealthProbeSetting is { } probe",
    "<HealthProbeEditor",
    "else if (IsBackendResilienceSection)",
    "<BackendResilienceEditor",
    "else if (IsCircuitBreakerSection)",
    "<CircuitBreakerEditor",
    "else if (IsLoadBalancingSection)",
    "<LoadBalancingEditor",
    "string.Equals(SelectedSection, \"Logging\", StringComparison.OrdinalIgnoreCase)",
    "<LoggingEditor",
    "string.Equals(SelectedSection, \"Identity & Access\", StringComparison.OrdinalIgnoreCase)",
    "<IdentityAccessEditor",
    "else if (IsHeaderPolicySection)",
    "<HeaderPolicyEditor",
    "string.Equals(SelectedSection, \"Request\", StringComparison.OrdinalIgnoreCase)",
    "<RequestEditor",
    "else if (IsRequestProcessingSection)",
    "<RequestProcessingEditor",
    "string.Equals(SelectedSection, \"Response\", StringComparison.OrdinalIgnoreCase)",
    "<ResponseEditor",
    "string.Equals(SelectedSection, \"Server\", StringComparison.OrdinalIgnoreCase)",
    "<ServerEditor",
    "class=\"table table-sm align-middle config-table mb-0\"",
]);
Check(!editor.Contains("TokenomicsEditor", StringComparison.Ordinal), "Tokenomics stays outside the modal editor switch");
Check(!editor.Contains("\"Async\"", StringComparison.Ordinal), "Async has no dedicated editor branch");
Check(page.Contains("<TokenomicsEditor", StringComparison.Ordinal), "Tokenomics remains an inline editor");

var expected = ExpectedSections();
Check(AppConfigurationEditorCatalog.Sections.Count == expected.Count, "catalog section count");
for (var index = 0; index < expected.Count; index++)
{
    if (index >= AppConfigurationEditorCatalog.Sections.Count)
    {
        break;
    }

    var actual = AppConfigurationEditorCatalog.Sections[index];
    var spec = expected[index];
    Check(actual == spec, $"{spec.Name} descriptor");
}

Check(AppConfigurationEditorCatalog.Generic.Editor == AppConfigurationEditorCatalog.GenericEditor, "generic editor");
Check(AppConfigurationEditorCatalog.Generic.ModalIcon is null, "generic icon");
Check(AppConfigurationEditorCatalog.Generic.SubtitleKind == AppConfigurationEditorCatalog.SubtitleSettingCount, "generic subtitle");
Check(!AppConfigurationEditorCatalog.TryGet("General", out _), "generic name is not a named section");
Check(!AppConfigurationEditorCatalog.TryGet(null, out _), "null section");
Check(!AppConfigurationEditorCatalog.TryGet("", out _), "empty section");
Check(!AppConfigurationEditorCatalog.TryGet("Health Probe", out _), "spaced HealthProbe");
Check(!AppConfigurationEditorCatalog.TryGet("circuit breaker", out _), "spaced CircuitBreaker");
Check(!AppConfigurationEditorCatalog.TryGet("load balancing", out _), "spaced LoadBalancing");
Check(!AppConfigurationEditorCatalog.TryGet("local change review", out _), "Local change review remains case-sensitive");
Check(AppConfigurationEditorCatalog.IsSection("hosts", "Hosts"), "Hosts ignores case");
Check(AppConfigurationEditorCatalog.IsSection("HealthProbe", "HealthProbe"), "HealthProbe exact name");
Check(!AppConfigurationEditorCatalog.IsSection(null, "Hosts"), "null predicate");
Check(!AppConfigurationEditorCatalog.IsSection("Server", "Hosts"), "predicate does not cross sections");

RequirePredicate(page, "IsHostSection", "SelectedSection", "Hosts");
RequirePredicate(page, "IsRequestProcessingSection", "SelectedSection", "Request Processing");
RequirePredicate(page, "IsHeaderPolicySection", "SelectedSection", "Header Policy");
RequirePredicate(page, "IsBackendResilienceSection", "SelectedSection", "Backend Resilience");
RequirePredicate(page, "IsProxyRuntimeSection", "SelectedSection", "Proxy Runtime");
RequirePredicate(page, "IsBackendRoutingSection", "SelectedSection", "Backend Routing");
RequirePredicate(page, "IsHealthProbeSectionName", "section", "HealthProbe");
RequirePredicate(page, "IsTokenomicsSectionName", "section", "Tokenomics");
RequirePredicate(page, "IsCircuitBreakerSectionName", "section", "CircuitBreaker");
RequirePredicate(page, "IsLoadBalancingSectionName", "section", "LoadBalancing");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("editor catalog spec: ok");
return 0;

static List<AppConfigurationEditorDescriptor> ExpectedSections() =>
[
    new("Local change review", "ChangeReview", null, null, AppConfigurationEditorCatalog.SubtitleChangedCount, DedicatedBranch: true, OrdinalName: true),
    new("Hosts", "HostSectionEditor", null, "Hosts", AppConfigurationEditorCatalog.SubtitleSettingCount),
    new("Proxy Runtime", "ProxyRuntimeEditor", "bi-gear-fill", "Server", AppConfigurationEditorCatalog.SubtitleStatic,
        "Configure process settings, health reporting, and runtime behavior."),
    new("Backend Routing", "BackendRoutingEditor", "bi-diagram-3-fill", "LoadBalancing", AppConfigurationEditorCatalog.SubtitleStatic,
        "Select, attempt, and communicate with backend targets."),
    new("HealthProbe", "HealthProbeEditor", null, "HealthProbe", AppConfigurationEditorCatalog.SubtitleHealthProbeState),
    new("Backend Resilience", "BackendResilienceEditor", "bi-shield-fill-check", "CircuitBreaker", AppConfigurationEditorCatalog.SubtitleStatic,
        "Define backend success criteria and protect the proxy from unhealthy dependencies."),
    new("CircuitBreaker", "CircuitBreakerEditor", null, "CircuitBreaker", AppConfigurationEditorCatalog.SubtitleCircuitBreakerSummary),
    new("LoadBalancing", "LoadBalancingEditor", null, "LoadBalancing", AppConfigurationEditorCatalog.SubtitleLoadBalancingSummary),
    new("Logging", "LoggingEditor", null, "Logging", AppConfigurationEditorCatalog.SubtitleNone),
    new("Identity & Access", "IdentityAccessEditor", null, "Identity & Access", AppConfigurationEditorCatalog.SubtitleSettingCount),
    new("Header Policy", "HeaderPolicyEditor", "bi-file-earmark-text-fill", "Request", AppConfigurationEditorCatalog.SubtitleHeaderSet),
    new("Request", "RequestEditor", null, "Request", AppConfigurationEditorCatalog.SubtitleSettingCount),
    new("Request Processing", "RequestProcessingEditor", "bi-arrow-left-right", "Request", AppConfigurationEditorCatalog.SubtitleStatic,
        "Configure timeouts and request prioritization."),
    new("Response", "ResponseEditor", null, "Response", AppConfigurationEditorCatalog.SubtitleSettingCount),
    new("Server", "ServerEditor", null, "Server", AppConfigurationEditorCatalog.SubtitleSettingCount),
    new("Async", AppConfigurationEditorCatalog.GenericEditor, null, "Async", AppConfigurationEditorCatalog.SubtitleSettingCount, DedicatedBranch: false),
    new("Tokenomics", "TokenomicsEditor", null, null, AppConfigurationEditorCatalog.SubtitleNone, DedicatedBranch: false),
];

void RequirePredicate(string source, string helper, string argument, string section)
{
    var expected = $"AppConfigurationEditorCatalog.IsSection({argument}, \"{section}\")";
    Check(source.Contains(expected, StringComparison.Ordinal), $"{helper} uses {expected}");
}

void RequireOrder(string source, string region, string[] markers)
{
    var position = 0;
    foreach (var marker in markers)
    {
        var found = source.IndexOf(marker, position, StringComparison.Ordinal);
        if (found < 0)
        {
            failures.Add($"{region} missing or reordered: {marker}");
            return;
        }

        position = found + marker.Length;
    }
}

string Slice(string source, string start, string end)
{
    var startIndex = source.IndexOf(start, StringComparison.Ordinal);
    var endIndex = startIndex < 0 ? -1 : source.IndexOf(end, startIndex, StringComparison.Ordinal);
    if (startIndex < 0 || endIndex < 0)
    {
        failures.Add($"missing region {start}");
        return string.Empty;
    }

    return source[startIndex..endIndex];
}

void Check(bool condition, string name)
{
    if (!condition)
    {
        failures.Add(name);
    }
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src/CompanionApp/Components/Pages/AdminConfigurationPage.razor")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("Run this check from the repository.");
}
