using System;
using System.Threading.Tasks;
// This class represents the request received from the upstream client.
public class S7PRequeueException : Exception, IDisposable
{
    public S7PRequeueException(string message) : base(message)
    {
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
