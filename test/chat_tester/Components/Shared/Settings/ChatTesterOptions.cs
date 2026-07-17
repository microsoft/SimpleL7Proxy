namespace chat_tester.Components.Shared;

/// <summary>
/// Strongly typed settings for the chat tester, bound from the <c>chat-tester</c>
/// configuration section. Property initializers act as the defaults used when a
/// key is absent from configuration.
/// </summary>
public class ChatTesterOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "chat-tester";

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
