namespace SimpleL7Proxy.Proxy;

// This class represents the request received from the upstream client.
public class S7PClientReadException : Exception, IDisposable
{
    public RequestData Request { get; }

    public S7PClientReadException(string message, RequestData request, Exception innerException)
        : base(message, innerException)
    {
        Request = request;
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
