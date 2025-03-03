public interface IBackendService
{
    public Task Start();
    public void Stop();

    public List<BackendHost> _hosts { get; set; }
    public List<BackendHost> GetActiveHosts();

    public int ActiveHostCount();

    public Task WaitForStartup(int timeout);
    public string HostStatus();
    public void TrackStatus(int code, bool wasException);
    public bool CheckFailedStatus();
    public string OAuth2Token();
}

public interface IEventHubClient
{
    Task StartTimer();
    void StopTimer();
    void SendData(string? value);
    void SendData(Dictionary<string, string> eventData);
    int GetEntryCount();
    bool IsRunning { get; set; }
}

public interface IBackendOptions
{
    int[] AcceptableStatusCodes { get; set; }
    HttpClient? Client { get; set; }
    int CircuitBreakerErrorThreshold { get; set; }
    int CircuitBreakerTimeslice { get; set; }
    int DefaultPriority { get; set; }
    int DefaultTTLSecs { get; set; }
    List<string> DisallowedHeaders { get; set; }
    string HostName { get; set; }
    List<BackendHost>? Hosts { get; set; }
    string IDStr { get; set; }
    List<string> LogHeaders { get; set; }
    bool LogAllRequestHeaders { get; set; }
    bool LogAllResponseHeaders { get; set; }
    bool LogProbes { get; set; }
    string LookupHeaderName { get; set; }
    int MaxQueueLength { get; set; }
    string OAuthAudience { get; set; }
    int Port { get; set; }
    int PollInterval { get; set; }
    int PollTimeout { get; set; }
    string PriorityKeyHeader { get; set; }
    List<string> PriorityKeys { get; set; }
    List<int> PriorityValues { get; set; }
    Dictionary<int, int> PriorityWorkers { get; set; }
    List<string> RequiredHeaders { get; set; }
    int SuccessRate { get; set; }
    int Timeout { get; set; }
    int TerminationGracePeriodSeconds { get; set; }
    List<string> UniqueUserHeaders { get; set; }
    bool UseOAuth { get; set; }
    bool UseUserConfig { get; set; }
    bool UseProfiles { get; set; }
    string UserProfileHeader { get; set; }
    string UserConfigUrl { get; set; }
    float UserPriorityThreshold { get; set; }
    Dictionary<string, string> ValidateHeaders { get; set; }
    int Workers { get; set; }
}

public interface IUserPriority
{
    string GetState();
    Guid addRequest(string userId);
    bool removeRequest(string userId, Guid requestId);
    public bool boostIndicator(string userId, out float boostValue);
    public float threshold { get; set; }

}

public interface IUserProfile
{
    public Dictionary<string, string> GetUserProfile(string userId);

}