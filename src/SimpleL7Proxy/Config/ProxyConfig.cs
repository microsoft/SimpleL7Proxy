using System.Reflection;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;

namespace SimpleL7Proxy.Config;

// Each parameter is available to use as a config setting with the following metadata:
// ConfigName is only needed when the env var / config key differs from the
// C# property name.  When omitted, the property name is used as-is.
//
// Mode (defaults to Warm):
//   Warm   — hot-reloaded from App Configuration (~30 s), no restart needed.
//   Cold   — published to App Configuration but requires a restart to take effect.
//   Hidden — runtime-derived, parsed, or sensitive; never published to App Configuration.
public class ProxyConfig
{
    // ════════════════════════════════════════════════════════════════════
    // Warm — published to App Configuration, hot-reloaded (~30 s)
    // ════════════════════════════════════════════════════════════════════

    // ── Async ──
    [ConfigOption("Async:ClientConfigFieldName", ConfigName = "AsyncClientConfigFieldName")]
    public string AsyncClientConfigFieldName { get; set; } = "async-config";
    [ConfigOption("Request:Headers:AsyncMode", ConfigName = "AsyncClientRequestHeader")]
    public string AsyncClientRequestHeader { get; set; } = "S7PAsyncMode";
    [ConfigOption("Async:Timeout", ConfigName = "AsyncTimeout")]
    public double AsyncTimeout { get; set; } = 30 * 60000; // 30 minutes
    [ConfigOption("Async:TriggerTimeout", ConfigName = "AsyncTriggerTimeout")]
    public int AsyncTriggerTimeout { get; set; } = 10000; // 10 seconds
    [ConfigOption("Async:TTLSecs", ConfigName = "AsyncTTLSecs")]
    public int AsyncTTLSecs { get; set; } = 24 * 60 * 60; // 24 hours

    // ── Circuit Breaker ──
    [ConfigOption("CircuitBreaker:ErrorThreshold", ConfigName = "CBErrorThreshold")]
    public int CircuitBreakerErrorThreshold { get; set; } = 50;
    [ConfigOption("CircuitBreaker:Timeslice", ConfigName = "CBTimeslice")]
    public int CircuitBreakerTimeslice { get; set; } = 60;

    // ── Health Probe ──
    [ConfigOption("HealthProbe:Sidecar", ConfigName = "HealthProbeSidecar")]
    public string HealthProbeSidecar { get; set; } = "Enabled=false;url=http://localhost:9000";

    // ── Load Balancing ──
    [ConfigOption("LoadBalancing:IterationMode")]
    public IterationModeEnum IterationMode { get; set; } = IterationModeEnum.SinglePass;
    [ConfigOption("LoadBalancing:Mode")]
    public string LoadBalanceMode { get; set; } = Constants.Latency;
    [ConfigOption("LoadBalancing:MultiPass:MaxAttempts")]
    public int MaxAttempts { get; set; } = 10;

    // ── Logging ──
    [ConfigOption("Logging:LogAllRequestHeaders")]
    public bool LogAllRequestHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllRequestHeadersExcept")]
    public List<string> LogAllRequestHeadersExcept { get; set; } = ["Authorization"];
    [ConfigOption("Logging:LogAllResponseHeaders")]
    public bool LogAllResponseHeaders { get; set; } = false;
    [ConfigOption("Logging:LogAllResponseHeadersExcept")]
    public List<string> LogAllResponseHeadersExcept { get; set; } = ["Api-Key"];
    [ConfigOption("Logging:LogHeaders")]
    public List<string> LogHeaders { get; set; } = [];
    [ConfigOption("Logging:LogToAI")]
    public List<string> LogToAI { get; set; } = [""];
    [ConfigOption("Logging:LogToConsole")]
    public List<string> LogToConsole { get; set; } = ["*"];
    [ConfigOption("Logging:LogToEvents")]
    public List<string> LogToEvents { get; set; } = ["async","backend","probe","circuitbreaker","custom","exception","profile","proxy","enqueued","auth"];

    // ── Profiles ──
    [ConfigOption("Profiles:Auth:ConfigUrl")]
    public string ValidateAuthAppIDUrl { get; set; } = ""; // e.g. "file:authappids.json" or "http://configservice/authappids"
    [ConfigOption("Profiles:Auth:ValidateAppIDEnabled")]
    public bool ValidateAuthAppID { get; set; } = false;
    [ConfigOption("Profiles:Auth:ValidateAppIDHeader")]
    public string ValidateAuthAppIDHeader { get; set; } = "X-MS-CLIENT-PRINCIPAL-ID";
    [ConfigOption("Profiles:Auth:ValidateFieldName")]
    public string ValidateAuthAppFieldName { get; set; } = "authAppID";
    [ConfigOption("Profiles:SuspendedUser:ConfigUrl")]
    public string SuspendedUserConfigUrl { get; set; } = ""; // e.g. "file:suspended.json" or "http://configservice/suspended"
    [ConfigOption("Profiles:User:ConfigRequired")]
    public bool UserConfigRequired { get; set; } = false;
    [ConfigOption("Profiles:User:IDFieldName")]
    public string UserIDFieldName { get; set; } = "userId";
    [ConfigOption("Profiles:User:ProfileHeader")]
    public string UserProfileHeader { get; set; } = "X-UserProfile";
    [ConfigOption("Profiles:User:UseProfiles")]
    public bool UseProfiles { get; set; } = false;
    [ConfigOption("Profiles:User:UserConfigUrl")]
    public string UserConfigUrl { get; set; } = ""; // e.g. "file:users.json" or "http://configservice/users"

    // ── Request ──
    [ConfigOption("Request:DefaultTimeout")]
    public int Timeout { get; set; } = 60*20*1000; // 20 minutes
    [ConfigOption("Request:DefaultTTLSecs")]
    public int DefaultTTLSecs { get; set; } = 300;
    [ConfigOption("Request:DependancyHeaders")]
    public List<string> DependancyHeaders { get; set; } = ["Backend-Host", "Host-URL", "Status", "Duration", "Error", "Message", "Request-Date", "backendLog"];
    [ConfigOption("Request:DisallowedHeaders")]
    public List<string> DisallowedHeaders { get; set; } = [];
    [ConfigOption("Request:Headers:PriorityKeyHeader")]
    public string PriorityKeyHeader { get; set; } = "S7PPriorityKey";
    [ConfigOption("Request:Headers:TimeoutHeader")]
    public string TimeoutHeader { get; set; } = "S7PTimeout";
    [ConfigOption("Request:Headers:TTLHeader")]
    public string TTLHeader { get; set; } = "S7PTTL";
    [ConfigOption("Request:Headers:UniqueUserHeaders")]
    public List<string> UniqueUserHeaders { get; set; } = ["X-UserID"];
    [ConfigOption("Request:Headers:ValidateHeaders")]
    public Dictionary<string, string> ValidateHeaders { get; set; } = [];
    [ConfigOption("Request:Priority:DefaultPriority")]
    public int DefaultPriority { get; set; } = 2;
    [ConfigOption("Request:Priority:GreedyUserThreshold")]
    public float UserPriorityThreshold { get; set; } = 0.1f;
    [ConfigOption("Request:Priority:PriorityKeys")]
    public List<string> PriorityKeys { get; set; } = ["12345", "234"];
    [ConfigOption("Request:Priority:PriorityValues")]
    public List<int> PriorityValues { get; set; } = [1, 3];
    [ConfigOption("Request:RequiredHeaders")]
    public List<string> RequiredHeaders { get; set; } = [];
    [ConfigOption("Request:StripRequestHeaders")]
    public List<string> StripRequestHeaders { get; set; } = [];

    // ── Response ──
    [ConfigOption("Response:AcceptableStatusCodes")]
    public int[] AcceptableStatusCodes { get; set; } = [200, 202, 401, 403, 404, 408, 410, 412, 417, 400];
    [ConfigOption("Response:StripResponseHeaders")]
    public List<string> StripResponseHeaders { get; set; } = [];

    // ── Sentinel ──
    [ConfigOption("Sentinel")]
    public string Sentinel { get; set; } = ""; // used for app configrefresh

    // ════════════════════════════════════════════════════════════════════
    // Cold — published to App Configuration, requires restart
    // ════════════════════════════════════════════════════════════════════

    // ── Async ──
    [ConfigOption("Async:Storage:BlobConfig", ConfigName = "AsyncBlobStorageConfig", Mode = ConfigMode.Cold)]
    public string AsyncBlobStorageConfig { get; set; } = "uri=https://mystorageaccount.blob.core.windows.net,mi=true";
    [ConfigOption("Async:Storage:Workers", ConfigName = "AsyncBlobWorkerCount", Mode = ConfigMode.Cold)]
    public int AsyncBlobWorkerCount { get; set; } = 2;
    [ConfigOption("Async:Enabled", ConfigName = "AsyncModeEnabled", Mode = ConfigMode.Cold)]
    public bool AsyncModeEnabled { get; set;  } = false;
    [ConfigOption("Async:ServiceBus:Config", ConfigName = "AsyncSBConfig", Mode = ConfigMode.Cold)]
    public string AsyncSBConfig { get; set; } = "cs=example-sb-connection-string,ns=example-namespace,q=requeststatus,mi=false";
    [ConfigOption("Async:Storage:ContainerName", ConfigName = "StorageDbContainerName", Mode = ConfigMode.Cold)]
    public string StorageDbContainerName { get; set; } = "Requests";
    // [ConfigOption("Async:Storage:Enabled", ConfigName = "StorageDbEnabled", Mode = ConfigMode.Cold)]
    // public bool StorageDbEnabled { get; set; } = false;

    // ── Circuit Breaker ──
    [ConfigOption("CircuitBreaker:SuccessRate", Mode = ConfigMode.Cold)]
    public int SuccessRate { get; set; } = 80;

    // ── Logging ──
    [ConfigOption("Logging:AppInsightsConnectionString", ConfigName = "APPINSIGHTS_CONNECTIONSTRING", Mode = ConfigMode.Cold)]
    public string AppInsightsConnectionString { get; set; } = "";
    [ConfigOption("Logging:EventData", ConfigName = "EVENT_HEADERS", Mode = ConfigMode.Cold)]
    public string EventHeaders { get; set; } = "SimpleL7Proxy.Events.CommonEventHeaders";
    [ConfigOption("Logging:EventHub:ConnectionString", ConfigName = "EVENTHUB_CONNECTIONSTRING", Mode = ConfigMode.Cold)]
    public string EventHubConnectionString { get; set; } = "";
    [ConfigOption("Logging:EventHub:MaxReconnectAttempts", ConfigName = "EVENTHUB_MAX_RECONNECT_ATTEMPTS", Mode = ConfigMode.Cold)]
    public int EventHubMaxReconnectAttempts { get; set; } = 5;
    [ConfigOption("Logging:EventHub:Name", ConfigName = "EVENTHUB_NAME", Mode = ConfigMode.Cold)]
    public string EventHubName { get; set; } = "";
    [ConfigOption("Logging:EventHub:Namespace", ConfigName = "EVENTHUB_NAMESPACE", Mode = ConfigMode.Cold)]
    public string EventHubNamespace { get; set; } = "";
    [ConfigOption("Logging:EventHub:StartupSeconds", ConfigName = "EVENTHUB_STARTUP_SECONDS", Mode = ConfigMode.Cold)]
    public int EventHubStartupSeconds { get; set; } = 10;
    [ConfigOption("Logging:EventLoggers", ConfigName = "EVENT_LOGGERS", Mode = ConfigMode.Cold)]
    public string EventLoggers { get; set; } = "file";
    [ConfigOption("Logging:LogDateTime", ConfigName = "LOGDATETIME", Mode = ConfigMode.Cold)]
    public bool LogDateTime { get; set; } = false;
    [ConfigOption("Logging:LogFileName", ConfigName = "LOGFILE_NAME", Mode = ConfigMode.Cold)]
    public string LogFileName { get; set; } = "eventslog.json";
    [ConfigOption("Logging:ReuseEvents", Mode = ConfigMode.Cold)]
    public bool ReuseEvents { get; set; } = false;

    // ── Profiles ──
    [ConfigOption("Profiles:RefreshIntervalSecs", Mode = ConfigMode.Cold)]
    public int UserConfigRefreshIntervalSecs { get; set; } = 3600; // 1 hour
    [ConfigOption("Profiles:SoftDeleteTTLMinutes", Mode = ConfigMode.Cold)]
    public int UserSoftDeleteTTLMinutes { get; set; } = 360; // 6 hours

    // ── Security ──
    [ConfigOption("Security:IgnoreSSLCert", ConfigName = "IgnoreSSLCert", Mode = ConfigMode.Cold)]
    public bool IgnoreSSLCert { get; set; } = false;
    [ConfigOption("Security:OAuthAudience", Mode = ConfigMode.Cold)]
    public string OAuthAudience { get; set; } = "";
    [ConfigOption("Security:UseOAuth", Mode = ConfigMode.Cold)]
    public bool UseOAuth { get; set; } = false;

    // ── Server ──
    [ConfigOption("Server:GC2InternalSecs", ConfigName = "GC2InternalSecs", Mode = ConfigMode.Cold)]
    public int GC2InternalSecs { get; set; } = 300; // 5 minutes
    [ConfigOption("Server:MaxQueueLength", Mode = ConfigMode.Cold)]
    public int MaxQueueLength { get; set; } = 1000;
    [ConfigOption("Server:MaxUndrainedEvents", ConfigName = "EVENTHUB_MAX_UNDRAINED_EVENTS", Mode = ConfigMode.Cold)]
    public int MaxUndrainedEvents { get; set; } = 100;
    [ConfigOption("Server:PollInterval", Mode = ConfigMode.Cold)]
    public int PollInterval { get; set; } = 15000;
    [ConfigOption("Server:PollTimeout", Mode = ConfigMode.Cold)]
    public int PollTimeout { get; set; } = 3000;
    [ConfigOption("Server:Port", Mode = ConfigMode.Cold)]
    public int Port { get; set; } = 80;
    /// <summary>How often (in seconds) to run cleanup of stale shared iterators.</summary>
    [ConfigOption("Server:SharedIteratorCleanupIntervalSeconds", Mode = ConfigMode.Cold)]
    public int SharedIteratorCleanupIntervalSeconds { get; set; } = 30;
    /// <summary>How long (in seconds) an unused shared iterator lives before cleanup.</summary>
    [ConfigOption("Server:SharedIteratorTTLSeconds", Mode = ConfigMode.Cold)]
    public int SharedIteratorTTLSeconds { get; set; } = 60;
    [ConfigOption("Server:TerminationGracePeriodSeconds", ConfigName = "TERMINATION_GRACE_PERIOD_SECONDS", Mode = ConfigMode.Cold)]
    public int TerminationGracePeriodSeconds { get; set; } = 30;
    [ConfigOption("Server:Workers", Mode = ConfigMode.Cold)]
    public int Workers { get; set; } = 10;

    // ════════════════════════════════════════════════════════════════════
    // Hidden — not published (runtime-derived / parsed / composite)
    // ════════════════════════════════════════════════════════════════════

    // ── Security ──
    [ConfigOption("Security:UseOAuthGov", Mode = ConfigMode.Hidden)]
    public bool UseOAuthGov { get; set; } = false;

    // ── Server ──
    [ConfigOption("Server:UseSharedIterators", Mode = ConfigMode.Hidden)]
    public bool UseSharedIterators { get; set; } = true;

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
    [ConfigOption("Logging:Level", ConfigName = "LOG_LEVEL", Mode = ConfigMode.Hidden)]
    public string LogLevel { get; set; } = "Information";
    [ConfigOption("Logging:LogToFile", ConfigName = "LOGTOFILE", Mode = ConfigMode.Hidden)]
    public bool LogToFile { get; set; } = false;

    // ── App Config ──
    [ConfigOption("AppConfig:Endpoint", ConfigName = "AZURE_APPCONFIG_ENDPOINT", Mode = ConfigMode.Hidden)]
    public string? AppConfigEndpoint { get; set; }
    [ConfigOption("AppConfig:ConnectionString", ConfigName = "AZURE_APPCONFIG_CONNECTION_STRING", Mode = ConfigMode.Hidden)]
    public string? AppConfigConnectionString { get; set; }
    [ConfigOption("AppConfig:Label", ConfigName = "AZURE_APPCONFIG_LABEL", Mode = ConfigMode.Hidden)]
    public string? AppConfigLabel { get; set; }
    [ConfigOption("AppConfig:RefreshIntervalSeconds", ConfigName = "AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS", Mode = ConfigMode.Hidden)]
    public int AppConfigRefreshIntervalSeconds { get; set; } = 30;

    // ── Transport / Keep-Alive ──
    [ConfigOption("Transport:KeepAliveInitialDelaySecs", ConfigName = "KeepAliveInitialDelaySecs", Mode = ConfigMode.Hidden)]
    public int KeepAliveInitialDelaySecs { get; set; } = 60;
    [ConfigOption("Transport:KeepAlivePingIntervalSecs", ConfigName = "KeepAlivePingIntervalSecs", Mode = ConfigMode.Hidden)]
    public int KeepAlivePingIntervalSecs { get; set; } = 60;
    [ConfigOption("Transport:KeepAliveIdleTimeoutSecs", ConfigName = "KeepAliveIdleTimeoutSecs", Mode = ConfigMode.Hidden)]
    public int KeepAliveIdleTimeoutSecs { get; set; } = 1200;
    [ConfigOption("Transport:EnableMultipleHttp2Connections", ConfigName = "EnableMultipleHttp2Connections", Mode = ConfigMode.Hidden)]
    public bool EnableMultipleHttp2Connections { get; set; } = false;
    [ConfigOption("Transport:MultiConnLifetimeSecs", ConfigName = "MultiConnLifetimeSecs", Mode = ConfigMode.Hidden)]
    public int MultiConnLifetimeSecs { get; set; } = 3600;
    [ConfigOption("Transport:MultiConnIdleTimeoutSecs", ConfigName = "MultiConnIdleTimeoutSecs", Mode = ConfigMode.Hidden)]
    public int MultiConnIdleTimeoutSecs { get; set; } = 300;
    [ConfigOption("Transport:MultiConnMaxConns", ConfigName = "MultiConnMaxConns", Mode = ConfigMode.Hidden)]
    public int MultiConnMaxConns { get; set; } = 4000;

    // ── Metadata ──
    [ConfigOption("Metadata:ContainerApp", ConfigName = "CONTAINER_APP_NAME", Mode = ConfigMode.Hidden)]
    public string ContainerApp { get; set; } = "ContainerAppName";
    [ConfigOption("Metadata:HostName", ConfigName = "Hostname", Mode = ConfigMode.Hidden)]
    public string HostName { get; set; } = "";
    [ConfigOption("Metadata:IDStr", ConfigName = "RequestIDPrefix", Mode = ConfigMode.Hidden)]
    public string IDStr { get; set; } = "S7P";
    [ConfigOption("Metadata:ReplicaName", ConfigName = "CONTAINER_APP_REPLICA_NAME", Mode = ConfigMode.Hidden)]
    public string ReplicaName { get; set; } = "";
    [ConfigOption("Metadata:Revision", ConfigName = "CONTAINER_APP_REVISION", Mode = ConfigMode.Hidden)]
    public string Revision { get; set; } = "revisionID";

    // ── Runtime-derived (no attribute) ──
    public HttpClient? Client { get; set; }
    public bool HealthProbeSidecarEnabled { get; set; } = false;
    public string HealthProbeSidecarUrl { get; set; } = "http://localhost/9000";
    public List<HostConfig> Hosts { get; set; } = [];
    public Dictionary<int, int> PriorityWorkers { get; set; } = new() { { 2, 1 }, { 3, 1 } };
    public bool TrackWorkers { get; set; } = true;

    /// <summary>
    /// Creates a deep copy of this instance. Scalar properties are copied directly;
    /// collections (List, Dictionary, array) are cloned so the copy is fully independent.
    /// Note: <see cref="Client"/> (HttpClient) is shared, not cloned.
    /// </summary>
    public ProxyConfig DeepClone()
    {
        var clone = (ProxyConfig)MemberwiseClone();

        // Clone collection properties so mutations don't leak between instances.
        clone.AcceptableStatusCodes = (int[])AcceptableStatusCodes.Clone();
        clone.DependancyHeaders = new List<string>(DependancyHeaders);
        clone.DisallowedHeaders = new List<string>(DisallowedHeaders);
        clone.LogAllRequestHeadersExcept = new List<string>(LogAllRequestHeadersExcept);
        clone.LogAllResponseHeadersExcept = new List<string>(LogAllResponseHeadersExcept);
        clone.LogHeaders = new List<string>(LogHeaders);
        clone.LogToConsole = new List<string>(LogToConsole);
        clone.LogToEvents = new List<string>(LogToEvents);
        clone.LogToAI = new List<string>(LogToAI);
        clone.PriorityKeys = new List<string>(PriorityKeys);
        clone.PriorityValues = new List<int>(PriorityValues);
        clone.RequiredHeaders = new List<string>(RequiredHeaders);
        clone.StripRequestHeaders = new List<string>(StripRequestHeaders);
        clone.StripResponseHeaders = new List<string>(StripResponseHeaders);
        clone.UniqueUserHeaders = new List<string>(UniqueUserHeaders);
        clone.ValidateHeaders = new Dictionary<string, string>(ValidateHeaders);
        clone.PriorityWorkers = new Dictionary<int, int>(PriorityWorkers);
        clone.Hosts = new List<HostConfig>(Hosts);

        return clone;
    }

    /// <summary>
    /// Applies a single configuration field from the incoming settings to this instance.
    /// Looks up <paramref name="configKey"/> in <paramref name="incomingSettings"/>; when absent
    /// or set to the default placeholder, the property is left unchanged.
    /// The caller is expected to seed this instance (e.g. via DeepClone) before calling.
    /// </summary>
    public void ApplyFieldFromEnv(Dictionary<string, string> incomingSettings, string configKey, string propertyName)
    {
        var prop = typeof(ProxyConfig).GetProperty(propertyName) ?? throw new InvalidOperationException($"Unknown ProxyConfig property: {propertyName}");
        var currentValue = prop.GetValue(this);
        var type = prop.PropertyType;

        var rawIncoming = incomingSettings.GetValueOrDefault(configKey)?.Trim();
        bool hasIncomingValue = !string.IsNullOrEmpty(rawIncoming) && rawIncoming != ConfigMetadata.DefaultPlaceholder;
        if (!hasIncomingValue)
            return;

        object? resolved = type switch
        {
            _ when type == typeof(int)
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, Convert.ToInt32(currentValue)),
            _ when type == typeof(double)
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, Convert.ToInt32(currentValue)),
            _ when type == typeof(float)
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, Convert.ToSingle(currentValue)),
            _ when type == typeof(string)
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, (string)currentValue!),
            _ when type == typeof(bool)
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, (bool)currentValue!),
            _ when type == typeof(List<string>)
                => ConfigParser.ToListOfString(
                       ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, string.Join(",", (List<string>)currentValue!))),
            _ when type == typeof(List<int>)
                => ConfigParser.ToListOfInt(
                       ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, string.Join(",", (List<int>)currentValue!))),
            _ when type == typeof(int[])
                => ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, (int[])currentValue!),
            _ when type == typeof(Dictionary<string, string>)
                => ConfigParser.KVStringPairs(ConfigParser.ToListOfString(
                       ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey,
                           string.Join(",", ((Dictionary<string, string>)currentValue!).Select(kvp => $"{kvp.Key}={kvp.Value}"))))),
            _ when type.IsEnum
                => Enum.TryParse(type, ConfigParser.ReadEnvironmentVariableOrDefault(incomingSettings, configKey, currentValue!.ToString()!), true, out var parsed)
                    ? parsed : currentValue,
            _ => throw new NotSupportedException($"ApplyFieldFromEnv: unsupported property type {type.Name} for {propertyName}")
        };

        prop.SetValue(this, resolved);
    }
}