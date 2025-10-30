namespace SimpleL7Proxy.Backend;

public interface ICircuitBreaker
{
    public string ID { get; set; } 
    void TrackStatus(int code, bool wasException);
    bool CheckFailedStatus();
}