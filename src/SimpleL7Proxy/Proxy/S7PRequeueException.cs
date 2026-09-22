namespace SimpleL7Proxy.Proxy;
using Proxy;

// This class represents the request received from the upstream client.
public class S7PRequeueException: Exception, IDisposable
{
    //public ProxyData pr { get; set; }
    public int RetryAfter { get; set; } = 0;
    public bool now=false;
    public S7PRequeueException(string message, int retry_after) : base(message)
    {
        RetryAfter = retry_after;
    }

    public S7PRequeueException(string message, bool now=true, int retry_after=0) : base(message)
    {
        //pr = pd;
        this.now = now;
        RetryAfter = retry_after;
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
