namespace SimpleL7Proxy.Backend;

public interface ICircuitBreaker
{
    public string ID { get; set; } 
    void TrackStatus(int code, bool wasFailure, string state);
    
    Task<bool> CheckFailedStatusAsync(bool nosleep=false);

    /// <summary>
    /// Removes this instance from the global circuit-breaker counters.
    /// Must be called exactly once when the owning host is retired.
    /// </summary>
    void Deregister();

    public string GetCircuitBreakerStatusString();
}