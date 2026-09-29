namespace SimpleL7Proxy.Proxy;
using Proxy;

// This class represents the request received from the upstream client.
public class S7PThrottledException: Exception, IDisposable
{
    //public ProxyData pr { get; set; }
    public int RetryAfter { get; set; } = 0;
    public bool now=false;
    public S7PThrottledException(string message, ProxyData pd, int retry_after) : base(message)
    {
        //pr = pd;
        RetryAfter = retry_after;
    }

    public S7PThrottledException(string message, bool now=true) : base(message)
    {
        //pr = pd;
        this.now = now;
        RetryAfter = now ? 0 : 1;
    }



    public void Dispose()
    {
        // Dispose of unmanaged resources here
    }
    void IDisposable.Dispose()
    {
        // TODO: Dispose of unmanaged resources here
    }

    public ValueTask DisposeAsync()
    {
        ((IDisposable)this).Dispose();
        return ValueTask.CompletedTask;
    }
}
