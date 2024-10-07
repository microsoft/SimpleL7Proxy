public class BackendOptions : IBackendOptions
{
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int SuccessRate { get; set; }
    public int PollTimeout { get; set; }
    public int Timeout { get; set; }

    public int Workers { get; set; }
    public List<string> PriorityKeys { get; set; } = new List<string>();
    public List<int> PriorityValues { get; set; } = new List<int>();
    public int DefaultPriority { get; set; }
    public int MaxQueueLength { get; set; }
    public string OAuthAudience { get; set; } = "";
    public bool UseOAuth { get; set; }

    public List<BackendHost>? Hosts { get; set; }
    public HttpClient? Client { get; set; }

}