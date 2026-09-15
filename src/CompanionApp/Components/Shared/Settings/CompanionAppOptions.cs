namespace CompanionApp.Components.Shared;

/// <summary>
/// Strongly typed settings for CompanionApp. Operational settings bind from
/// <c>CompanionApp</c>; presentation settings bind from <c>CompanionAppUI</c>.
/// Property initializers act as the defaults used when a key is absent from configuration.
/// </summary>
public class CompanionAppOptions
{
    /// <summary>Configuration section for operational settings.</summary>
    public const string SectionName = "CompanionApp";

    /// <summary>Configuration section for presentation settings and help content.</summary>
    public const string UiSectionName = "CompanionAppUI";

    public string AppConfigurationEndpoint { get; set; } = string.Empty;

    public string AppConfigurationLabel { get; set; } = string.Empty;

    /// <summary>Dev-only: read/write the App Configuration snapshot from disk to skip the live download.</summary>
    public bool BypassConfig { get; set; }

    /// <summary>Config-driven guidance for grouping/customizing published settings (e.g. "Host*" -> "Hosts").</summary>
    public List<AppConfigurationSettingRule> AppConfigurationRules { get; set; } = new();

    /// <summary>Host-specific presentation settings for the Proxy Configuration admin page.</summary>
    public AppConfigHostSettings Hosts { get; set; } = new();

    public string ServerBaseUrl { get; set; } = "http://localhost:8080";

    public string DefaultMethod { get; set; } = "GET";

    public string ChatEndpointPath { get; set; } = "/openai/v1/chat/completions";

    public string ChatRequestBody { get; set; } =
        "{\"model\":\"gpt-4o\",\"messages\":[{\"role\":\"user\",\"content\":\"tell me a joke\"}],\"stream\":true}";

    public int RequestCount { get; set; } = 100;

    public int AbortDelayMilliseconds { get; set; } = 25;

    public string Payload { get; set; } = "{\"message\":\"abort-before-response\"}";

    public string[] TargetUrls { get; set; } =
    {
        "/",
        "/struts2-showcase/struts/utils.js",
        "/js/?$%7Bjndi:dns://MDEDiscovery17ed960289SeenInTheWildGet-8000%7D",
        "/:undefined",
        "/struts2-showcase/struts/tooltip.gif",
        "/struts2-showcase/struts/domtt.css",
        "/struts2-showcase/struts/domTT.js",
        "/struts2-showcase/token/transfer4.action",
        "/struts2-showcase/struts/inputtransfersselect.js",
        "/struts2-showcase/struts/optiontransferselect.js",
        "/struts2-showcase/$%7Bjndi:dns://MDEDiscovery17ed960289ApacheStruts2-8000%7D",
        "/index.action/struts/utils.js",
        "/api",
        "/login",
        "/admin",
        "/robots.txt"
    };

    public string AuthorizationHeaderName { get; set; } = "Authorization";

    public string AuthorizationHeaderPrefix { get; set; } = "Bearer";

    public string[] AuthTargetUrls { get; set; } =
    {
        "/api",
        "/api/v1",
        "/api/v1/users",
        "/login",
        "/admin",
        "/oauth/token",
        "/health",
        "/swagger"
    };

    public string UserHeaderName { get; set; } = "x-user-id";

    public string PriorityKeyHeader { get; set; } = "S7PPriorityKey";

    public string[] UserNames { get; set; } =
    {
        "alice",
        "bob",
        "carol",
        "dave"
    };

    /// <summary>
    /// Default custom request headers, each as a <c>Name: Value</c> string. Use the
    /// <c>{id}</c> token in a value to insert the sequential request number.
    /// </summary>
    public string[] DefaultHeaders { get; set; } = System.Array.Empty<string>();

    public HistoryStorageSettings History { get; set; } = new();

    public ConversationStorageSettings Conversations { get; set; } = new();
}

public sealed class HistoryStorageSettings : IStorageSettings
{
    public string Mode { get; set; } = HistoryStorageMode.Disk;

    public string DiskPath { get; set; } = string.Empty;

    public string StorageAccountName { get; set; } = string.Empty;

    public string BlobContainerName { get; set; } = "history";

    public string CosmosAccount { get; set; } = string.Empty;

    public string CosmosDatabase { get; set; } = string.Empty;

    public string CosmosContainer { get; set; } = string.Empty;
}

public static class HistoryStorageMode
{
    public const string Disk = "Disk";
    public const string BlobStorage = "BlobStorage";
    public const string CosmosDb = "CosmosDb";
}

/// <summary>
/// Config-driven guidance for a group of published settings. The <see cref="Match"/> glob is
/// tested against the key path after the <c>Warm:</c>/<c>Cold:</c> prefix; matching settings are
/// placed in <see cref="Section"/>. Supports <c>prefix*</c>, <c>*suffix</c>, <c>*contains*</c>, or exact.
/// </summary>
public sealed class AppConfigurationSettingRule
{
    public string Match { get; set; } = string.Empty;

    public string? Section { get; set; }
}

/// <summary>
/// Host-specific presentation settings for the Proxy Configuration admin page: dropdown
/// choices for host table columns (e.g. "mode", "processor") and the docs URL used by the
/// New host dialog. Processor choices mirror StreamProcessorFactory.
/// </summary>
public sealed class AppConfigHostSettings
{
    public Dictionary<string, string[]> FieldChoices { get; set; } = new();

    public string FieldsDocUrl { get; set; } = string.Empty;

    /// <summary>Columns shown in the Hosts list table (the host name is always the first column).</summary>
    public string[] ListColumns { get; set; } = System.Array.Empty<string>();
}
