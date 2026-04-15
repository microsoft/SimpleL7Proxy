namespace SimpleL7Proxy.Events;

/// <summary>
/// Per-event-type enable/disable flags for a single log destination.
/// Constructed from a config list (e.g. ["*"], ["BackendRequest","Exception"]).
/// </summary>
public class LogTargetAttr
{
    public bool Async;
    public bool BackendRequest;
    public bool Probe;
    public bool Poller;
    public bool CircuitBreakerError;
    public bool Console;
    public bool CustomEvent;
    public bool Exception;
    public bool ProfileError;
    public bool ProxyRequest;
    public bool ProxyRequestEnqueued;
    public bool Authentication;

    /// <summary>
    /// Returns whether the given <see cref="EventType"/> is enabled for this destination.
    /// </summary>
    public bool IsEnabled(EventType type) => type switch
    {
        EventType.AsyncProcessing                                => Async,
        EventType.Backend or EventType.BackendRequest            => BackendRequest,
        EventType.Poller                                         => Poller,
        EventType.Probe                                          => Probe,
        EventType.CircuitBreakerError                            => CircuitBreakerError,
        EventType.Console                                        => Console,
        EventType.CustomEvent                                    => CustomEvent,
        EventType.Exception or EventType.ServerError             => Exception,
        EventType.ProfileError                                   => ProfileError,
        EventType.ProxyError 
            or EventType.ProxyRequest or EventType.ProxyRequestExpired
            or EventType.ProxyRequestRequeued                    => ProxyRequest,
        EventType.ProxyRequestEnqueued                           => ProxyRequestEnqueued,
        EventType.Authentication                                 => Authentication,
        _                                                        => true,
    };

    /// <summary>
    /// Creates a <see cref="LogTargetAttr"/> from a config list.
    /// A list containing "*" enables all event types.
    /// </summary>
    public static LogTargetAttr From(List<string>? configList)
    {
        var list = configList ?? [];
        bool all = list.Contains("*");
        var set = all ? null : new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

        return new LogTargetAttr
        {
            Async            = all || set!.Contains("async"),
            BackendRequest   = all || set!.Contains("backend"),
            Probe            = all || set!.Contains("probe"),
            Poller           = all || set!.Contains("poller"),  
            CircuitBreakerError = all || set!.Contains("circuitbreaker"),
            Console          = all || set!.Contains("console"),
            CustomEvent      = all || set!.Contains("custom"),
            Exception        = all || set!.Contains("exception"),
            ProfileError     = all || set!.Contains("profile"),
            ProxyRequest     = all || set!.Contains("proxy"),
            ProxyRequestEnqueued = all || set!.Contains("enqueued"),
            Authentication   = all || set!.Contains("auth"),
        };
    }

    public string ToString()
    {
        return $"Async: {Async}, BackendRequest: {BackendRequest}, Probe: {Probe}, CircuitBreakerError: {CircuitBreakerError}, Console: {Console}, CustomEvent: {CustomEvent}, Exception: {Exception}, ProfileError: {ProfileError}, ProxyRequest: {ProxyRequest}, ProxyRequestEnqueued: {ProxyRequestEnqueued}, Authentication: {Authentication}";
    }

}
