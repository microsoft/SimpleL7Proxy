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
    public bool Metric;

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
        EventType.Metric                                         => Metric,
        _                                                        => true,
    };

    /// <summary>
    /// Creates a <see cref="LogTargetAttr"/> from a config list.
    /// A list containing "*" enables all event types.
    /// An entry prefixed with "-" excludes that event type, even when "*" is present
    /// (e.g. ["*","-custom"] enables everything except CustomEvent).
    /// </summary>
    public static LogTargetAttr From(List<string>? configList)
    {
        var list = configList ?? [];
        bool all = list.Contains("*");

        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list)
        {
            if (string.IsNullOrWhiteSpace(item) || item == "*")
            {
                continue;
            }

            if (item.StartsWith('-'))
            {
                excludes.Add(item[1..]);
            }
            else
            {
                includes.Add(item);
            }
        }

        bool On(string key) => (all || includes.Contains(key)) && !excludes.Contains(key);

        return new LogTargetAttr
        {
            Async            = On("async"),
            BackendRequest   = On("backend"),
            Probe            = On("probe"),
            Poller           = On("poller"),
            CircuitBreakerError = On("circuitbreaker"),
            Console          = On("console"),
            CustomEvent      = On("custom"),
            Exception        = On("exception"),
            ProfileError     = On("profile"),
            ProxyRequest     = On("proxy"),
            ProxyRequestEnqueued = On("enqueued"),
            Authentication   = On("auth"),
            Metric           = On("metric"),
        };
    }

    public override string ToString()
    {
        return $"Async: {Async}, BackendRequest: {BackendRequest}, Probe: {Probe}, CircuitBreakerError: {CircuitBreakerError}, Console: {Console}, CustomEvent: {CustomEvent}, Exception: {Exception}, ProfileError: {ProfileError}, ProxyRequest: {ProxyRequest}, ProxyRequestEnqueued: {ProxyRequestEnqueued}, Authentication: {Authentication}, Metric: {Metric}";
    }

}
