using System.Collections.Concurrent;

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
    int Count { get; }
    Task StartTimer();
    Task StopTimer();
    void SendData(string? value);
    void SendData(ProxyEvent eventData);
    int GetEntryCount();
    bool IsRunning { get; set; }
}

public interface IBackendOptions
{
    int[] AcceptableStatusCodes { get; set; }
    HttpClient? Client { get; set; }
    string ContainerApp { get; set; }
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
    List<string> LogAllRequestHeadersExcept { get; set; }
    bool LogAllResponseHeaders { get; set; }
    List<string> LogAllResponseHeadersExcept { get; set; }
    bool LogConsole { get; set; }
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
    string Revision { get; set; }
    List<string> RequiredHeaders { get; set; }
    int SuccessRate { get; set; }
    string SuspendedUserConfigUrl { get; set; }
    int Timeout { get; set; }
    string TimeoutHeader { get; set; }
    int TerminationGracePeriodSeconds { get; set; }
    string TTLHeader { get; set; }
    List<string> UniqueUserHeaders { get; set; }
    bool UseOAuth { get; set; }
    bool UseOAuthGov { get; set; }
    bool UseUserConfig { get; set; }
    bool UseProfiles { get; set; }
    string UserProfileHeader { get; set; }
    string UserConfigUrl { get; set; }
    float UserPriorityThreshold { get; set; }
    Dictionary<string, string> ValidateHeaders { get; set; }
    bool ValidateAuthAppID { get; set; }
    string ValidateAuthAppFieldName { get; set; }
    string ValidateAuthAppIDUrl { get; set; }
    string ValidateAuthAppIDHeader { get; set; }
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
    public bool IsUserSuspended(string userId);
    public bool IsAuthAppIDValid(string authAppId);

}