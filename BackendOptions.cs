public class BackendOptions : IBackendOptions
{
    public int Port { get; set; }
    public int PollInterval { get; set; }
    public int SuccessRate { get; set; }
    public int PollTimeout { get; set; }
    public int Timeout { get; set; }

    public int Workers { get; set; }
    public string PriorityKey1 { get; set; } = "";
    public string PriorityKey2 { get; set; } ="";
    public string OAuthAudience { get; set; } = "";
    public bool UseOAuth { get; set; }

    public List<BackendHost>? Hosts { get; set; }
    public HttpClient? Client { get; set; }

}