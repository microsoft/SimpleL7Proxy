namespace SimpleL7Proxy.Backend;

using System.Net.Http.Headers;

public interface ICircuitBreaker
{
    public string ID { get; set; } 
    void TrackStatus(int code, bool wasFailure, string state, HttpResponseHeaders? responseHeaders = null);
    
    public int GetBackpressureDelay();
    
    public int GetMsToNextRetry();

    /// <summary>
    /// Removes this instance from the global circuit-breaker counters.
    /// Must be called exactly once when the owning host is retired.
    /// </summary>
    void Deregister();

    public string GetCircuitBreakerStatusString();
}