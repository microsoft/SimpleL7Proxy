namespace SimpleL7Proxy.Events;

public enum EventType
{
    AsyncProcessing,
    Backend,
    BackendRequest,
    CircuitBreakerError,
    Console,
    CustomEvent,
    Exception,
    Poller,
    Probe,
    ProfileError,
    ProxyError,
    ProxyRequest,
    ProxyRequestEnqueued,
    ProxyRequestExpired,
    ProxyRequestRequeued,
    ServerError,
    Authentication,
}