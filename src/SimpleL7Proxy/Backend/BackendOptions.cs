namespace SimpleL7Proxy.Backend;

public class BackendOptions
{
    public int[] AcceptableStatusCodes { get; set; } =[];
    public string AsyncBlobStorageConnectionString { get; set; } = "example-connection-string"; 
    public string AsyncClientBlobFieldname { get; set; } = "async-blobname";
    public string AsyncClientBlobTimeoutFieldName { get; set; } = "async-blobaccess-timeout";
    public string AsyncClientAllowedFieldName { get; set; } = "async-allowed";
    public bool AsyncModeEnabled { get; set; } = false;
    public string AsyncSBConnectionString { get; set; } = "example-sb-connection-string";
    public string AsyncSBTopicFieldName { get; set; } = "async-topic";
    public double AsyncTimeout { get; set; } = 30 * 60000; // 30 minutes
    public HttpClient? Client { get; set; }
    public string ContainerApp { get; set; } = "";
    public int CircuitBreakerErrorThreshold { get; set; }
    public int CircuitBreakerTimeslice { get; set; }
    public int DefaultPriority { get; set; }
    public int DefaultTTLSecs { get; set; }
    public List<string> DisallowedHeaders { get; set; } = [];
    public string HostName { get; set; } = "";
    public List<BackendHostConfig> Hosts { get; set; } = [];
    public string IDStr { get; set; } = "S7P";
    public List<string> LogHeaders { get; set; } = [];
    public bool LogConsoleEvent { get; set; } = true;
    public bool LogProbes { get; set; }
    public bool LogAllRequestHeaders { get; set; } = false;
    public List<string> LogAllRequestHeadersExcept { get; set; } = [];
    public bool LogAllResponseHeaders { get; set; } = false;
    public List<string> LogAllResponseHeadersExcept { get; set; } = [];
    public string UserIDFieldName { get; set; } = "";
    public int MaxQueueLength { get; set; }
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