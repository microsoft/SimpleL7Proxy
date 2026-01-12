using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;

namespace SimpleL7Proxy.Config;

public class BackendOptions
{
    public int[] AcceptableStatusCodes { get; set; } = [];
    public string AsyncBlobStorageConnectionString { get; set; } = "example-connection-string";
    public bool AsyncBlobStorageUseMI { get; set; } = true;
    public string AsyncBlobStorageAccountUri { get; set; } = "https://mystorageaccount.blob.core.windows.net";
    public int AsyncBlobWorkerCount { get; set; } = 2;
    public string AsyncClientRequestHeader { get; set; } = "AsyncMode";
    public string AsyncClientConfigFieldName { get; set; } = "async-config";
    public bool AsyncModeEnabled { get; set; } = false;
    public string AsyncSBConnectionString { get; set; } = "example-sb-connection-string";
    public string AsyncSBQueue { get; set; } = "requeststatus";
    public bool AsyncSBUseMI { get; set; } = false; // Use managed identity for Service Bus
    public string AsyncSBNamespace { get; set; } = "example-namespace";
    public double AsyncTimeout { get; set; } = 30 * 60000; // 30 minutes
    public int AsyncTTLSecs { get; set; } = 24 * 60 * 60; // 24 hours
    public int AsyncTriggerTimeout { get; set; } = 60000; // 1 minute
    public HttpClient? Client { get; set; }
    public string ContainerApp { get; set; } = "";
    public int CircuitBreakerErrorThreshold { get; set; }
    public int CircuitBreakerTimeslice { get; set; }
    public int DefaultPriority { get; set; }
    public int DefaultTTLSecs { get; set; }
    public string[] DependancyHeaders { get; set; } = [];
    public List<string> DisallowedHeaders { get; set; } = [];
    public string HealthProbeSidecar { get; set; } = "Enabled=false; Url=http://localhost:9000";
    public bool HealthProbeSidecarEnabled { get; set; } = false; 
    public string HealthProbeSidecarUrl { get; set; } = "http://localhost/9000";
    public string HostName { get; set; } = "";
    public List<HostConfig> Hosts { get; set; } = [];
    public string IDStr { get; set; } = "S7P";
    public IterationModeEnum IterationMode { get; set; }
    public string LoadBalanceMode { get; set; } = "latency"; // "latency", "roundrobin", "random"
    public bool LogConsole { get; set; }
    public bool LogConsoleEvent { get; set; }
    public bool LogPoller { get; set; } = false;
    public List<string> LogHeaders { get; set; } = [];
    public bool LogProbes { get; set; }
    public bool LogAllRequestHeaders { get; set; } = false;
    public List<string> LogAllRequestHeadersExcept { get; set; } = [];
    public bool LogAllResponseHeaders { get; set; } = false;
    public List<string> LogAllResponseHeadersExcept { get; set; } = [];
    public string UserIDFieldName { get; set; } = "";
    public int MaxQueueLength { get; set; }
    public int MaxAttempts { get ; set; }
    public string OAuthAudience { get; set; } = "";
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int PollTimeout { get; set; }
    public string PriorityKeyHeader { get; set; } = "";
    public List<string> PriorityKeys { get; set; } = [];
    public List<int> PriorityValues { get; set; } = [];
    public Dictionary<int, int> PriorityWorkers { get; set; } = [];
    public string Revision { get; set; } = "";
    public List<string> RequiredHeaders { get; set; } = [];
    public int SuccessRate { get; set; }
    public string SuspendedUserConfigUrl { get; set; } = "";
    // Storage configuration
    public bool StorageDbEnabled { get; set; } = false;
    public string StorageDbContainerName { get; set; } = "Requests";
    public List<string> StripResponseHeaders { get; set; } = [];
    public List<string> StripRequestHeaders { get; set; } = [];
    public int Timeout { get; set; }
    public string TimeoutHeader { get; set; } = "";
    public int TerminationGracePeriodSeconds { get; set; }
    public string TTLHeader { get; set; } = "";
    public List<string> UniqueUserHeaders { get; set; } = [];
    public bool UseOAuth { get; set; }
    public bool UseOAuthGov { get; set; } = false;
    public bool UseProfiles { get; set; } = false;
    public string UserProfileHeader { get; set; } = "";
    public string UserConfigUrl { get; set; } = "";
    public float UserPriorityThreshold { get; set; }
    public Dictionary<string, string> ValidateHeaders { get; set; } = [];
    public bool ValidateAuthAppID { get; set; } = false;
    public string ValidateAuthAppIDUrl { get; set; } = "";
    public string ValidateAuthAppFieldName { get; set; } = "";
    public string ValidateAuthAppIDHeader { get; set; } = "";
    public int Workers { get; set; }
}