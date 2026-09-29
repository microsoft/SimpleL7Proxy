namespace SimpleL7Proxy.Proxy;
using Proxy;

// This class represents the request received from the upstream client.
public class S7PRejectedException: Exception, IDisposable
{
    //public ProxyData pr { get; set; }
    public S7PRejectedException(string message) : base(message)
    {
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
