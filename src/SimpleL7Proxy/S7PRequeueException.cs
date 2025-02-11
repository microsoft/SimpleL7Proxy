namespace SimpleL7Proxy;
using Proxy;

// This class represents the request received from the upstream client.
public class S7PRequeueException: Exception, IDisposable
{
    public ProxyData pr { get; set; }
    public S7PRequeueException(string message, ProxyData pd) : base(message)
    {
        pr = pd;
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
