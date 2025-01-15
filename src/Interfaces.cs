public interface IBackendService
{
    void Start(CancellationToken cancellationToken);
    public List<BackendHost> _hosts { get; set; }
    public List<BackendHost> GetActiveHosts();

    public int ActiveHostCount();

    public Task waitForStartup(int timeout);
    public string HostStatus();
    public void TrackStatus(int code);
    public bool CheckFailedStatus();
    public string OAuth2Token();
}

public interface IEventHubClient
{
    void StartTimer();
    void StopTimer();
    void SendData(string? value);
    void SendData(Dictionary<string, string> eventData);
}

public interface IBackendOptions {
    HttpClient? Client { get; set; }
    int DefaultPriority { get; set; }
    int DefaultTTLSecs { get; set; }
    List<BackendHost>? Hosts { get; set; }
    string IDStr { get; set; }
    int MaxQueueLength { get; set; }
    int Port { get; set; }
    int PollInterval { get; set; }
    int PollTimeout { get; set; }
    List<string> PriorityKeys { get; set; }
    List<int> PriorityValues { get; set; }
    int SuccessRate { get; set; }
    int Timeout { get; set; }
    string UserConfigUrl { get; set; }
    float UserPriorityThreshold { get; set; }
    int Workers { get; set; }
}

public interface IUserPriority
{
    Guid addRequest(string userId);
    bool removeRequest(string userId, Guid requestId);
    public bool boostIndicator(string userId, out float boostValue);
    public float threshold { get; set; }

}