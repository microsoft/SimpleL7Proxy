public class BackendOptions : IBackendOptions
{
    public HttpClient? Client { get; set; }
    public int DefaultPriority { get; set; }
    public int DefaultTTLSecs { get; set; }
    public string HostName { get; set; } = "";
    public List<BackendHost>? Hosts { get; set; }
    public string IDStr { get; set; } = "S7P";
    public List<string> LogHeaders { get; set; } = new List<string>();
    public int MaxQueueLength { get; set; }
    public string OAuthAudience { get; set; } = "";
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int PollTimeout { get; set; }
    public List<string> PriorityKeys { get; set; } = new List<string>();
    public List<int> PriorityValues { get; set; } = new List<int>();
    public int SuccessRate { get; set; }
    public int Timeout { get; set; }
    public bool UseOAuth { get; set; }
    public int Workers { get; set; }
}