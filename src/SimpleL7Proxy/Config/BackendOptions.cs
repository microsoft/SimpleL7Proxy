using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;

namespace SimpleL7Proxy.Config;

public class BackendOptions
{
    // ════════════════════════════════════════════════════════════════════
    // Warm — published to App Configuration, hot-reloaded (~30 s)
    // ════════════════════════════════════════════════════════════════════

    // ── Async ──
    [ConfigOption("Async:ClientRequestHeader", ConfigName = "AsyncClientRequestHeader")]
    public string AsyncClientRequestHeader { get; set; } = "AsyncMode";
    [ConfigOption("Async:ClientConfigFieldName", ConfigName = "AsyncClientConfigFieldName")]
    public string AsyncClientConfigFieldName { get; set; } = "async-config";
    [ConfigOption("Async:Timeout", ConfigName = "AsyncTimeout")]
    public double AsyncTimeout { get; set; } = 30 * 60000; // 30 minutes
    [ConfigOption("Async:TTLSecs", ConfigName = "AsyncTTLSecs")]
    public int AsyncTTLSecs { get; set; } = 24 * 60 * 60; // 24 hours
    [ConfigOption("Async:TriggerTimeout", ConfigName = "AsyncTriggerTimeout")]
    public int AsyncTriggerTimeout { get; set; } = 10000; // 10 seconds

    // ── Circuit Breaker ──
    [ConfigOption("CircuitBreaker:ErrorThreshold", ConfigName = "CBErrorThreshold")]
    public int CircuitBreakerErrorThreshold { get; set; } = 50;
    [ConfigOption("CircuitBreaker:Timeslice", ConfigName = "CBTimeslice")]
    public int CircuitBreakerTimeslice { get; set; } = 60;

    // ── Health Probe ──
    [ConfigOption("HealthProbe:Sidecar", ConfigName = "HealthProbeSidecar")]
    public string HealthProbeSidecar { get; set; } = "Enabled=false;url=http://localhost:9000";

    // ── Load Balancing ──
    [ConfigOption("LoadBalancing:Mode")]
    public string LoadBalanceMode { get; set; } = "latency"; // "latency", "roundrobin", "random"

    // ── Logging ──
    [ConfigOption("Logging:LogToConsole")]
    public List<string> LogToConsole { get; set; } = ["*"];   
    [ConfigOption("Logging:LogToEvents")]
    public List<string> LogToEvents { get; set; } = ["async","backend","circuitbreaker","custom","exception","profile","proxy","enqueued","auth"];
    [ConfigOption("Logging:LogToAI")]
    public List<string> LogToAI { get; set; } = ["*"];

    public bool LogProbes { get; set; } = true;
    [ConfigOption("Logging:LogHeaders")]
    public List<string> LogHeaders { get; set; } = [];
    [ConfigOption("Logging:LogAllRequestHeaders")]
    public bool LogAllRequestHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllRequestHeadersExcept")]
    public List<string> LogAllRequestHeadersExcept { get; set; } = ["Authorization"];
    [ConfigOption("Logging:LogAllResponseHeaders")]
    public bool LogAllResponseHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllResponseHeadersExcept")]
    public List<string> LogAllResponseHeadersExcept { get; set; } = ["Api-Key"];

    // ── Priority ──
    [ConfigOption("Priority:DefaultPriority")]
    public int DefaultPriority { get; set; } = 2;
    [ConfigOption("Priority:DefaultTTLSecs")]
    public int DefaultTTLSecs { get; set; } = 300;
    [ConfigOption("Priority:PriorityKeyHeader")]
    public string PriorityKeyHeader { get; set; } = "S7PPriorityKey";
    [ConfigOption("Priority:PriorityKeys")]
    public List<string> PriorityKeys { get; set; } = ["12345", "234"];
    [ConfigOption("Priority:PriorityValues")]
    public List<int> PriorityValues { get; set; } = [1, 3];

    // ── Request ──
    [ConfigOption("Request:DependancyHeaders")]
    public List<string> DependancyHeaders { get; set; } = ["Backend-Host", "Host-URL", "Status", "Duration", "Error", "Message", "Request-Date", "backendLog"];
    [ConfigOption("Request:DisallowedHeaders")]
    public List<string> DisallowedHeaders { get; set; } = [];
    [ConfigOption("Request:MaxAttempts")]
    public int MaxAttempts { get; set; } = 10;
    [ConfigOption("Request:RequiredHeaders")]
    public List<string> RequiredHeaders { get; set; } = [];
    [ConfigOption("Request:StripRequestHeaders")]
    public List<string> StripRequestHeaders { get; set; } = [];
    [ConfigOption("Request:TimeoutHeader")]
    public string TimeoutHeader { get; set; } = "S7PTimeout";
    [ConfigOption("Request:TTLHeader")]
    public string TTLHeader { get; set; } = "S7PTTL";

    // ── Response ──
    [ConfigOption("Response:AcceptableStatusCodes")]
    public int[] AcceptableStatusCodes { get; set; } = [200, 202, 401, 403, 404, 408, 410, 412, 417, 400];
    [ConfigOption("Response:StripResponseHeaders")]
    public List<string> StripResponseHeaders { get; set; } = [];

    // ── User ──
    [ConfigOption("User:SuspendedUserConfigUrl")]
    public string SuspendedUserConfigUrl { get; set; } = "file:config.json";
    [ConfigOption("User:UniqueUserHeaders")]
    public List<string> UniqueUserHeaders { get; set; } = ["X-UserID"];
    [ConfigOption("User:UserConfigUrl")]
    public string UserConfigUrl { get; set; } = "file:config.json";
    [ConfigOption("User:UserIDFieldName")]
    public string UserIDFieldName { get; set; } = "userId";
    [ConfigOption("User:UserPriorityThreshold")]
    public float UserPriorityThreshold { get; set; } = 0.1f;
    [ConfigOption("User:UserProfileHeader")]
    public string UserProfileHeader { get; set; } = "X-UserProfile";

    // ── Validation ──
    [ConfigOption("Validation:ValidateHeaders")]
    public Dictionary<string, string> ValidateHeaders { get; set; } = [];
    [ConfigOption("Validation:ValidateAuthAppID")]
    public bool ValidateAuthAppID { get; set; } = false;
    [ConfigOption("Validation:ValidateAuthAppIDUrl")]
    public string ValidateAuthAppIDUrl { get; set; } = "file:auth.json";
    [ConfigOption("Validation:ValidateAuthAppFieldName")]
    public string ValidateAuthAppFieldName { get; set; } = "authAppID";
    [ConfigOption("Validation:ValidateAuthAppIDHeader")]
    public string ValidateAuthAppIDHeader { get; set; } = "X-MS-CLIENT-PRINCIPAL-ID";

    // ════════════════════════════════════════════════════════════════════
    // Cold — published to App Configuration, requires restart
    // ════════════════════════════════════════════════════════════════════

    // ── Async ──
    [ConfigOption("Async:BlobStorageConfig", ConfigName = "AsyncBlobStorageConfig", Mode = ConfigMode.Cold)]
    public string AsyncBlobStorageConfig { get; set; } = "uri=https://mystorageaccount.blob.core.windows.net,mi=true";
    [ConfigOption("Async:SBConfig", ConfigName = "AsyncSBConfig", Mode = ConfigMode.Cold)]
    public string AsyncSBConfig { get; set; } = "cs=example-sb-connection-string,ns=example-namespace,q=requeststatus,mi=false";
    [ConfigOption("Async:BlobWorkerCount", ConfigName = "AsyncBlobWorkerCount", Mode = ConfigMode.Cold)]
    public int AsyncBlobWorkerCount { get; set; } = 2;
    [ConfigOption("Async:ModeEnabled", ConfigName = "AsyncModeEnabled", Mode = ConfigMode.Cold)]
    public bool AsyncModeEnabled { get; set; } = false;

    // ── Security ──
    [ConfigOption("Security:OAuthAudience", Mode = ConfigMode.Cold)]
    public string OAuthAudience { get; set; } = "";
    [ConfigOption("Security:UseOAuth", Mode = ConfigMode.Cold)]
    public bool UseOAuth { get; set; } = false;
    [ConfigOption("Security:UseOAuthGov", Mode = ConfigMode.Cold)]
    public bool UseOAuthGov { get; set; } = false;

    // ── Server ──
    [ConfigOption("Server:IterationMode", Mode = ConfigMode.Cold)]
    public IterationModeEnum IterationMode { get; set; } = IterationModeEnum.SinglePass;
    [ConfigOption("Server:MaxQueueLength", Mode = ConfigMode.Cold)]
    public int MaxQueueLength { get; set; } = 1000;
    [ConfigOption("Server:PollInterval", Mode = ConfigMode.Cold)]
    public int PollInterval { get; set; } = 15000;
    [ConfigOption("Server:PollTimeout", Mode = ConfigMode.Cold)]
    public int PollTimeout { get; set; } = 3000;
    [ConfigOption("Server:Port", Mode = ConfigMode.Cold)]
    public int Port { get; set; } = 80;
    [ConfigOption("Server:SuccessRate", Mode = ConfigMode.Cold)]
    public int SuccessRate { get; set; } = 80;
    [ConfigOption("Server:TerminationGracePeriodSeconds", ConfigName = "TERMINATION_GRACE_PERIOD_SECONDS", Mode = ConfigMode.Cold)]
    public int TerminationGracePeriodSeconds { get; set; } = 30;
    [ConfigOption("Server:Timeout", Mode = ConfigMode.Cold)]
    public int Timeout { get; set; } = 1200000; // 20 minutes
    [ConfigOption("Server:Workers", Mode = ConfigMode.Cold)]
    public int Workers { get; set; } = 10;

    // ── Shared Iterators ──
    /// <summary>
    /// When true, requests to the same path share the same host iterator,
    /// ensuring fair round-robin distribution across concurrent requests.
    /// </summary>
    [ConfigOption("Server:UseSharedIterators", Mode = ConfigMode.Cold)]
    public bool UseSharedIterators { get; set; } = false;
    /// <summary>How long (in seconds) an unused shared iterator lives before cleanup.</summary>
    [ConfigOption("Server:SharedIteratorTTLSeconds", Mode = ConfigMode.Cold)]
    public int SharedIteratorTTLSeconds { get; set; } = 60;
    /// <summary>How often (in seconds) to run cleanup of stale shared iterators.</summary>
    [ConfigOption("Server:SharedIteratorCleanupIntervalSeconds", Mode = ConfigMode.Cold)]
    public int SharedIteratorCleanupIntervalSeconds { get; set; } = 30;

    // ── Storage ──
    [ConfigOption("Storage:Enabled", ConfigName = "StorageDbEnabled", Mode = ConfigMode.Cold)]
    public bool StorageDbEnabled { get; set; } = false;
    [ConfigOption("Storage:ContainerName", ConfigName = "StorageDbContainerName", Mode = ConfigMode.Cold)]
    public string StorageDbContainerName { get; set; } = "Requests";

    // ── User ──
    [ConfigOption("User:UseProfiles", Mode = ConfigMode.Cold)]
    public bool UseProfiles { get; set; } = false;
    [ConfigOption("User:UserConfigRequired", Mode = ConfigMode.Cold)]
    public bool UserConfigRequired { get; set; } = false;
    [ConfigOption("User:UserConfigRefreshIntervalSecs", Mode = ConfigMode.Cold)]
    public int UserConfigRefreshIntervalSecs { get; set; } = 3600; // 1 hour
    [ConfigOption("User:UserSoftDeleteTTLMinutes", Mode = ConfigMode.Cold)]
    public int UserSoftDeleteTTLMinutes { get; set; } = 360; // 6 hours

    // ════════════════════════════════════════════════════════════════════
    // Hidden — not published (runtime-derived / parsed / composite)
    // ════════════════════════════════════════════════════════════════════

    // ── Parsed from AsyncBlobStorageConfig ──
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageConnectionString", Mode = ConfigMode.Hidden)]
    public string AsyncBlobStorageConnectionString { get; set; } = "example-connection-string";
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageUseMI", Mode = ConfigMode.Hidden)]
    public bool AsyncBlobStorageUseMI { get; set; } = true;
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageAccountUri", Mode = ConfigMode.Hidden)]
    public string AsyncBlobStorageAccountUri { get; set; } = "https://mystorageaccount.blob.core.windows.net";

    // ── Parsed from AsyncSBConfig ──
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBConnectionString", Mode = ConfigMode.Hidden)]
    public string AsyncSBConnectionString { get; set; } = "example-sb-connection-string";
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBQueue", Mode = ConfigMode.Hidden)]
    public string AsyncSBQueue { get; set; } = "requeststatus";
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBUseMI", Mode = ConfigMode.Hidden)]
    public bool AsyncSBUseMI { get; set; } = false;
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBNamespace", Mode = ConfigMode.Hidden)]
    public string AsyncSBNamespace { get; set; } = "example-namespace";

    // ── Logging / Telemetry ──
    [ConfigOption("Logging:Level", ConfigName = "LOG_LEVEL", Mode = ConfigMode.Cold)]
    public string LogLevel { get; set; } = "Information";
    [ConfigOption("Logging:AppInsightsConnectionString", ConfigName = "APPINSIGHTS_CONNECTIONSTRING", Mode = ConfigMode.Cold)]
    public string AppInsightsConnectionString { get; set; } = "";
    [ConfigOption("Logging:EventLoggers", ConfigName = "EVENT_LOGGERS", Mode = ConfigMode.Cold)]
    public string EventLoggers { get; set; } = "file";
    [ConfigOption("Logging:LogToFile", ConfigName = "LOGTOFILE", Mode = ConfigMode.Hidden)]
    public bool LogToFile { get; set; } = false;
    [ConfigOption("Logging:LogFileName", ConfigName = "LOGFILE_NAME", Mode = ConfigMode.Cold)]
    public string LogFileName { get; set; } = "eventslog.json";

    // ── EventHub ──
    [ConfigOption("EventHub:ConnectionString", ConfigName = "EVENTHUB_CONNECTIONSTRING", Mode = ConfigMode.Cold)]
    public string EventHubConnectionString { get; set; } = "";
    [ConfigOption("EventHub:Name", ConfigName = "EVENTHUB_NAME", Mode = ConfigMode.Cold)]
    public string EventHubName { get; set; } = "";
    [ConfigOption("EventHub:Namespace", ConfigName = "EVENTHUB_NAMESPACE", Mode = ConfigMode.Cold)]
    public string EventHubNamespace { get; set; } = "";
    [ConfigOption("EventHub:StartupSeconds", ConfigName = "EVENTHUB_STARTUP_SECONDS", Mode = ConfigMode.Cold)]
    public int EventHubStartupSeconds { get; set; } = 10;

    // ── Security ──
    [ConfigOption("Security:IgnoreSSLCert", ConfigName = "IgnoreSSLCert", Mode = ConfigMode.Cold)]
    public bool IgnoreSSLCert { get; set; } = false;

    // ── Metadata ──
    [ConfigOption("Metadata:ContainerApp", ConfigName = "CONTAINER_APP_NAME", Mode = ConfigMode.Hidden)]
    public string ContainerApp { get; set; } = "ContainerAppName";
    [ConfigOption("Metadata:IDStr", ConfigName = "RequestIDPrefix", Mode = ConfigMode.Hidden)]
    public string IDStr { get; set; } = "S7P";
    [ConfigOption("Metadata:Revision", ConfigName = "CONTAINER_APP_REVISION", Mode = ConfigMode.Hidden)]
    public string Revision { get; set; } = "revisionID";

    // ── Runtime-derived (no attribute) ──
    public HttpClient? Client { get; set; }
    public bool HealthProbeSidecarEnabled { get; set; } = false;
    public string HealthProbeSidecarUrl { get; set; } = "http://localhost/9000";
    public string HostName { get; set; } = "";
    public List<HostConfig> Hosts { get; set; } = [];
    public Dictionary<int, int> PriorityWorkers { get; set; } = new() { { 2, 1 }, { 3, 1 } };
    public bool TrackWorkers { get; set; } = false;
}