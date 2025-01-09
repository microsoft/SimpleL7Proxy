using System;
using System.Threading.Tasks;

using SimpleL7Proxy.Proxy;
// This class represents the request received from the upstream client.
public class S7PRequeueException : Exception, IDisposable
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

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
