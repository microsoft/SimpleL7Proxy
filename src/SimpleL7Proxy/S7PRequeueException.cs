namespace SimpleL7Proxy;
using Proxy;

// This class represents the request received from the upstream client.
public class S7PRequeueException(string message, ProxyData pd)
    : Exception(message), IDisposable
{
    public ProxyData Pr { get; set; } = pd;

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
