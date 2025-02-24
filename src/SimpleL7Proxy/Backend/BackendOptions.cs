namespace SimpleL7Proxy.Backend;

public class BackendOptions
{
    public int[] AcceptableStatusCodes { get; set; } =[];
    public HttpClient? Client { get; set; }
    public int CircuitBreakerErrorThreshold { get; set; }
    public int CircuitBreakerTimeslice { get; set; }
    public int DefaultPriority { get; set; }
    public int DefaultTTLSecs { get; set; }
    public string HostName { get; set; } = "";
    public List<BackendHostConfig> Hosts { get; set; } = [];
    public string IDStr { get; set; } = "S7P";
    public List<string> LogHeaders { get; set; } = [];
    public bool LogProbes { get; set; }
    public int MaxQueueLength { get; set; }
    public string OAuthAudience { get; set; } = "";
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int PollTimeout { get; set; }
    public List<string> PriorityKeys { get; set; } = [];
    public List<int> PriorityValues { get; set; } = [];
    public Dictionary<int, int> PriorityWorkers { get; set; } = new Dictionary<int, int>();
    public List<string> RequiredHeaders { get; set; } = [];
    public int SuccessRate { get; set; }
    public int Timeout { get; set; }
    public int TerminationGracePeriodSeconds { get; set; }
    public List<string> UniqueUserHeaders { get; set; } = [];
    public bool UseOAuth { get; set; }
    public bool UseUserConfig { get; set; } = false;
    public bool UseProfiles { get; set; } = false;
    public string UserProfileHeader { get; set; } = "";
    public string UserConfigUrl { get; set; } = "";
    public float UserPriorityThreshold { get; set; }
    public int Workers { get; set; }

}