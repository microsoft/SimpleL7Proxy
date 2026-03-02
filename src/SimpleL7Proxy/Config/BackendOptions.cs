using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;

namespace SimpleL7Proxy.Config;

public class BackendOptions
{
    [ConfigOption("Response:AcceptableStatusCodes")]
    public int[] AcceptableStatusCodes { get; set; } = [200, 202, 401, 403, 404, 408, 410, 412, 417, 400];
    [ConfigOption("Async:BlobStorageConfig", ConfigName = "AsyncBlobStorageConfig")]
    public string AsyncBlobStorageConfig { get; set; } = "uri=https://mystorageaccount.blob.core.windows.net,mi=true";
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageConnectionString", Mode = ConfigMode.Hidden)]
    public string AsyncBlobStorageConnectionString { get; set; } = "example-connection-string";
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageUseMI", Mode = ConfigMode.Hidden)]
    public bool AsyncBlobStorageUseMI { get; set; } = true;
    [ParsedConfig("AsyncBlobStorageConfig")]
    [ConfigOption("Async:BlobStorageAccountUri", Mode = ConfigMode.Hidden)]
    public string AsyncBlobStorageAccountUri { get; set; } = "https://mystorageaccount.blob.core.windows.net";
    public int AsyncBlobWorkerCount { get; set; } = 2;
    [ConfigOption("Async:ClientRequestHeader", ConfigName = "AsyncClientRequestHeader")]
    public string AsyncClientRequestHeader { get; set; } = "AsyncMode";
    [ConfigOption("Async:ClientConfigFieldName", ConfigName = "AsyncClientConfigFieldName")]
    public string AsyncClientConfigFieldName { get; set; } = "async-config";
    public bool AsyncModeEnabled { get; set; } = false;
    [ConfigOption("Async:SBConfig", ConfigName = "AsyncSBConfig")]
    public string AsyncSBConfig { get; set; } = "cs=example-sb-connection-string,ns=example-namespace,q=requeststatus,mi=false";
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBConnectionString", Mode = ConfigMode.Hidden)]
    public string AsyncSBConnectionString { get; set; } = "example-sb-connection-string";
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBQueue", Mode = ConfigMode.Hidden)]
    public string AsyncSBQueue { get; set; } = "requeststatus";
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBUseMI", Mode = ConfigMode.Hidden)]
    public bool AsyncSBUseMI { get; set; } = false; // Use managed identity for Service Bus
    [ParsedConfig("AsyncSBConfig")]
    [ConfigOption("Async:SBNamespace", Mode = ConfigMode.Hidden)]
    public string AsyncSBNamespace { get; set; } = "example-namespace";
    [ConfigOption("Async:Timeout", ConfigName = "AsyncTimeout")]
    public double AsyncTimeout { get; set; } = 30 * 60000; // 30 minutes
    [ConfigOption("Async:TTLSecs", ConfigName = "AsyncTTLSecs")]
    public int AsyncTTLSecs { get; set; } = 24 * 60 * 60; // 24 hours
    [ConfigOption("Async:TriggerTimeout", ConfigName = "AsyncTriggerTimeout")]
    public int AsyncTriggerTimeout { get; set; } = 10000; // 10 seconds

    public HttpClient? Client { get; set; }

    [ConfigOption("Metadata:ContainerApp", ConfigName = "CONTAINER_APP_NAME")]
    public string ContainerApp { get; set; } = "ContainerAppName";

    [ConfigOption("CircuitBreaker:ErrorThreshold", ConfigName = "CBErrorThreshold")]
    public int CircuitBreakerErrorThreshold { get; set; } = 50;
    [ConfigOption("CircuitBreaker:Timeslice", ConfigName = "CBTimeslice")]
    public int CircuitBreakerTimeslice { get; set; } = 60;
    [ConfigOption("Priority:DefaultPriority")]
    public int DefaultPriority { get; set; } = 2;
    [ConfigOption("Priority:DefaultTTLSecs")]
    public int DefaultTTLSecs { get; set; } = 300;
    [ConfigOption("Request:DependancyHeaders")]
    public string[] DependancyHeaders { get; set; } = ["Backend-Host", "Host-URL", "Status", "Duration", "Error", "Message", "Request-Date", "backendLog"];
    [ConfigOption("Request:DisallowedHeaders")]
    public List<string> DisallowedHeaders { get; set; } = [];

    [ConfigOption("HealthProbe:Sidecar", ConfigName = "HealthProbeSidecar")]
    public string HealthProbeSidecar { get; set; } = "Enabled=false;url=http://localhost:9000";

    public bool HealthProbeSidecarEnabled { get; set; } = false; 

    public string HealthProbeSidecarUrl { get; set; } = "http://localhost/9000";

    public string HostName { get; set; } = "";

    public List<HostConfig> Hosts { get; set; } = [];
    [ConfigOption("Metadata:IDStr", ConfigName = "RequestIDPrefix", Mode = ConfigMode.Hidden)]
    public string IDStr { get; set; } = "S7P";

    public IterationModeEnum IterationMode { get; set; } = IterationModeEnum.SinglePass;

    [ConfigOption("LoadBalancing:Mode")]
    public string LoadBalanceMode { get; set; } = "latency"; // "latency", "roundrobin", "random"
    [ConfigOption("Logging:LogConsole")]
    public bool LogConsole { get; set; } = true;
    [ConfigOption("Logging:LogConsoleEvent")]
    public bool LogConsoleEvent { get; set; } = false;
    [ConfigOption("Logging:LogPoller")]
    public bool LogPoller { get; set; } = true;
    [ConfigOption("Logging:LogHeaders")]
    public List<string> LogHeaders { get; set; } = [];
    [ConfigOption("Logging:LogProbes")]
    public bool LogProbes { get; set; } = true;
    [ConfigOption("Logging:LogAllRequestHeaders")]
    public bool LogAllRequestHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllRequestHeadersExcept")]
    public List<string> LogAllRequestHeadersExcept { get; set; } = ["Authorization"];
    [ConfigOption("Logging:LogAllResponseHeaders")]
    public bool LogAllResponseHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllResponseHeadersExcept")]
    public List<string> LogAllResponseHeadersExcept { get; set; } = ["Api-Key"];
    [ConfigOption("User:UserIDFieldName")]
    public string UserIDFieldName { get; set; } = "userId";
    public int MaxQueueLength { get; set; } = 1000;
    [ConfigOption("Request:MaxAttempts")]
    public int MaxAttempts { get ; set; } = 10;
    public string OAuthAudience { get; set; } = "";
    public int Port { get; set; } = 80;
    public int PollInterval { get; set; } = 15000;
    public int PollTimeout { get; set; } = 3000;
    [ConfigOption("Priority:PriorityKeyHeader")]
    public string PriorityKeyHeader { get; set; } = "S7PPriorityKey";
    [ConfigOption("Priority:PriorityKeys")]
    public List<string> PriorityKeys { get; set; } = ["12345", "234"];
    [ConfigOption("Priority:PriorityValues")]
    public List<int> PriorityValues { get; set; } = [1, 3];
    public Dictionary<int, int> PriorityWorkers { get; set; } = new() { { 2, 1 }, { 3, 1 } };
    [ConfigOption("Metadata:Revision", ConfigName = "CONTAINER_APP_REVISION")]
    public string Revision { get; set; } = "revisionID";
    [ConfigOption("Request:RequiredHeaders")]
    public List<string> RequiredHeaders { get; set; } = [];
    public int SuccessRate { get; set; } = 80;
    [ConfigOption("User:SuspendedUserConfigUrl")]
    public string SuspendedUserConfigUrl { get; set; } = "file:config.json";
    // Storage configuration
    public bool StorageDbEnabled { get; set; } = false;
    public string StorageDbContainerName { get; set; } = "Requests";
    [ConfigOption("Response:StripResponseHeaders")]
    public List<string> StripResponseHeaders { get; set; } = [];
    [ConfigOption("Request:StripRequestHeaders")]
    public List<string> StripRequestHeaders { get; set; } = [];
    public int Timeout { get; set; } = 1200000; // 20 minutes
    [ConfigOption("Request:TimeoutHeader")]
    public string TimeoutHeader { get; set; } = "S7PTimeout";
    public int TerminationGracePeriodSeconds { get; set; } = 30;
    public bool TrackWorkers { get; set; } = false;
    [ConfigOption("Request:TTLHeader")]
    public string TTLHeader { get; set; } = "S7PTTL";
    [ConfigOption("User:UniqueUserHeaders")]
    public List<string> UniqueUserHeaders { get; set; } = ["X-UserID"];
    public bool UseOAuth { get; set; } = false;
    public bool UseOAuthGov { get; set; } = false;
    public bool UseProfiles { get; set; } = false;
    [ConfigOption("User:UserProfileHeader")]
    public string UserProfileHeader { get; set; } = "X-UserProfile";
    [ConfigOption("User:UserConfigUrl")]
    public string UserConfigUrl { get; set; } = "file:config.json";
    public bool UserConfigRequired { get; set; } = false;
    public int UserConfigRefreshIntervalSecs { get; set; } = 3600; // 1 hour
    [ConfigOption("User:UserPriorityThreshold")]
    public float UserPriorityThreshold { get; set; } = 0.1f;
    public int UserSoftDeleteTTLMinutes { get; set; } = 360; // 6 hours
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
    public int Workers { get; set; } = 10;
    
    // Shared Iterator Settings
    /// <summary>
    /// When true, requests to the same path share the same host iterator,
    /// ensuring fair round-robin distribution across concurrent requests.
    /// Default: false (each request gets its own iterator)
    /// </summary>
    public bool UseSharedIterators { get; set; } = false;
    
    /// <summary>
    /// How long (in seconds) an unused shared iterator lives before cleanup.
    /// Default: 60 seconds
    /// </summary>
    public int SharedIteratorTTLSeconds { get; set; } = 60;
    
    /// <summary>
    /// How often (in seconds) to run cleanup of stale shared iterators.
    /// Default: 30 seconds
    /// </summary>
    public int SharedIteratorCleanupIntervalSeconds { get; set; } = 30;
}