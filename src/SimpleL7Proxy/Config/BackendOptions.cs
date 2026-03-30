using System.Reflection;
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

    // ── Server ──
    [ConfigOption("Server:IterationMode")]
    public IterationModeEnum IterationMode { get; set; } = IterationModeEnum.SinglePass;
    [ConfigOption("Server:Timeout")]
    public int Timeout { get; set; } = 60*20*1000; // 20 minutes

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
    public string SuspendedUserConfigUrl { get; set; } = ""; // e.g. "file:suspended.json" or "http://configservice/suspended"
    [ConfigOption("User:UniqueUserHeaders")]
    public List<string> UniqueUserHeaders { get; set; } = ["X-UserID"];
    [ConfigOption("User:UserConfigUrl")]
    public string UserConfigUrl { get; set; } = ""; // e.g. "file:users.json" or "http://configservice/users"
    [ConfigOption("User:UserIDFieldName")]
    public string UserIDFieldName { get; set; } = "userId";
    [ConfigOption("User:UserPriorityThreshold")]
    public float UserPriorityThreshold { get; set; } = 0.1f;
    [ConfigOption("User:UserProfileHeader")]
    public string UserProfileHeader { get; set; } = "X-UserProfile";
    [ConfigOption("User:UseProfiles")]
    public bool UseProfiles { get; set; } = false;

    // ── Validation ──
    [ConfigOption("Validation:ValidateHeaders")]
    public Dictionary<string, string> ValidateHeaders { get; set; } = [];
    [ConfigOption("Validation:ValidateAuthAppID")]
    public bool ValidateAuthAppID { get; set; } = false;
    [ConfigOption("Validation:ValidateAuthAppIDUrl")]
    public string ValidateAuthAppIDUrl { get; set; } = ""; // e.g. "file:authappids.json" or "http://configservice/authappids"
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
    [ConfigOption("Security:IgnoreSSLCert", ConfigName = "IgnoreSSLCert", Mode = ConfigMode.Cold)]
    public bool IgnoreSSLCert { get; set; } = false;

    // ── Server ──
    [ConfigOption("Server:MaxQueueLength", Mode = ConfigMode.Cold)]
    public int MaxQueueLength { get; set; } = 1000;
    [ConfigOption("Server:MaxEvents", Mode = ConfigMode.Cold)]
    public int MaxEvents { get; set; } = 100000;
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
    [ConfigOption("Server:Workers", Mode = ConfigMode.Cold)]
    public int Workers { get; set; } = 10;

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
    [ConfigOption("User:UserConfigRequired", Mode = ConfigMode.Cold)]
    public bool UserConfigRequired { get; set; } = false;
    [ConfigOption("User:UserConfigRefreshIntervalSecs", Mode = ConfigMode.Cold)]
    public int UserConfigRefreshIntervalSecs { get; set; } = 3600; // 1 hour
    [ConfigOption("User:UserSoftDeleteTTLMinutes", Mode = ConfigMode.Cold)]
    public int UserSoftDeleteTTLMinutes { get; set; } = 360; // 6 hours

    // ── Logging / Telemetry ──
    [ConfigOption("Logging:AppInsightsConnectionString", ConfigName = "APPINSIGHTS_CONNECTIONSTRING", Mode = ConfigMode.Cold)]
    public string AppInsightsConnectionString { get; set; } = "";
    [ConfigOption("Logging:EventLoggers", ConfigName = "EVENT_LOGGERS", Mode = ConfigMode.Cold)]
    public string EventLoggers { get; set; } = "file";
    [ConfigOption("Logging:EventData", ConfigName = "EVENT_HEADERS", Mode = ConfigMode.Cold)]
    public string EventHeaders { get; set; } = "SimpleL7Proxy.Events.CommonEventHeaders";
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
    [ConfigOption("EventHub:MaxReconnectAttempts", ConfigName = "EVENTHUB_MAX_RECONNECT_ATTEMPTS", Mode = ConfigMode.Cold)]
    public int EventHubMaxReconnectAttempts { get; set; } = 5;
    [ConfigOption("EventHub:MaxUndrainedEvents", ConfigName = "EVENTHUB_MAX_UNDRAINED_EVENTS", Mode = ConfigMode.Cold)]
    public int MaxUndrainedEvents { get; set; } = 100;

    // ════════════════════════════════════════════════════════════════════
    // Hidden — not published (runtime-derived / parsed / composite)
    // ════════════════════════════════════════════════════════════════════

    // ── Security ──
    [ConfigOption("Security:UseOAuthGov", Mode = ConfigMode.Hidden)]
    public bool UseOAuthGov { get; set; } = false;

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

    // ── Shared Iterators ──
    /// <summary>
    /// When true, requests to the same path share the same host iterator,
    /// ensuring fair round-robin distribution across concurrent requests.
    /// </summary>
    [ConfigOption("Server:UseSharedIterators", Mode = ConfigMode.Hidden)]
    public bool UseSharedIterators { get; set; } = true;

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
    public bool TrackWorkers { get; set; } = true;

    /// <summary>
    /// Creates a deep copy of this instance. Scalar properties are copied directly;
    /// collections (List, Dictionary, array) are cloned so the copy is fully independent.
    /// Note: <see cref="Client"/> (HttpClient) is shared, not cloned.
    /// </summary>
    public BackendOptions DeepClone()
    {
        var clone = (BackendOptions)MemberwiseClone();

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
    /// Applies a single configuration field from the environment dictionary to this instance.
    /// Uses reflection to set the named property, falling back to the corresponding default value when the
    /// environment variable is absent or set to the default placeholder.
    /// </summary>
    public void ApplyFieldFromEnv(Dictionary<string, string> env, BackendOptions defaults, string envVar, string property)
    {
        var pi = typeof(BackendOptions).GetProperty(property) ?? throw new InvalidOperationException($"Unknown BackendOptions property: {property}");
        var defVal = pi.GetValue(defaults);
        var type = pi.PropertyType;

        var envValue = env.GetValueOrDefault(envVar)?.Trim();
        bool envVarPresent = !string.IsNullOrEmpty(envValue) && envValue != ConfigOptions.DefaultPlaceholder;
        if (!envVarPresent)
        {
            var currentVal = pi.GetValue(this);
            bool alreadyChanged = !Equals(currentVal, defVal);
            if (alreadyChanged)
            {
                return;
            }
        }

        if (type == typeof(int) || type == typeof(double))
        {
            var val = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, Convert.ToInt32(defVal));
            pi.SetValue(this, Convert.ChangeType(val, type));
        }
        else if (type == typeof(float))
        {
            var val = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, Convert.ToSingle(defVal));
            pi.SetValue(this, Convert.ChangeType(val, type));
        }
        else if (type == typeof(string))
        {
            pi.SetValue(this, ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, (string)defVal!));
        }
        else if (type == typeof(bool))
        {
            pi.SetValue(this, ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, (bool)defVal!));
        }
        else if (type == typeof(List<string>))
        {
            var value = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, string.Join(",", (List<string>)defVal!));
            pi.SetValue(this, ConfigParser.ToListOfString(value));
        }
        else if (type == typeof(List<int>))
        {
            var value = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, string.Join(",", (List<int>)defVal!));
            pi.SetValue(this, ConfigParser.ToListOfInt(value));
        }
        else if (type == typeof(int[]))
        {
            pi.SetValue(this, ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, (int[])defVal!));
        }
        else if (type == typeof(Dictionary<string, string>))
        {
            var defaultValue = string.Join(",", ((Dictionary<string, string>)defVal!).Select(kvp => $"{kvp.Key}={kvp.Value}"));
            var value = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, defaultValue);
            pi.SetValue(this, ConfigParser.KVStringPairs(ConfigParser.ToListOfString(value)));
        }
        else if (type.IsEnum)
        {
            var value = ConfigParser.ReadEnvironmentVariableOrDefault(env, envVar, defVal!.ToString()!);
            if (Enum.TryParse(type, value, true, out var parsed))
            {
                pi.SetValue(this, parsed);
            }
            else
            {
                pi.SetValue(this, defVal);
            }
        }
        else
        {
            throw new NotSupportedException($"ApplyFieldFromEnv: unsupported property type {type.Name} for {property}");
        }
    }
}