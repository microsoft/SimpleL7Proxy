using System;
using System.Threading.Tasks;
// This class represents the request received from the upstream client.
public class S7PRequeueException : Exception, IDisposable
{

    public ProxyData pr { get; set; }
    public int RetryAfter { get; set; } = 0;
    public S7PRequeueException(string message, ProxyData pd, int retry_after) : base(message)
    {
        pr = pd;
        RetryAfter = retry_after;
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
