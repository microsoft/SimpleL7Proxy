using System.Net;

public class ProxyErrorException(ProxyErrorException.ErrorType type, HttpStatusCode statusCode, string message) : Exception(message), IDisposable {
    // Define internal ENUM
    public enum ErrorType
    {
        InvalidTTL,
        TTLExpired,
        NotProcessed,
        ClientDisconnected,
        BackendDisconnected,
        IncompleteHeaders,
        InvalidHeader,
        DisallowedAppID,
        UnknownProfile
    }
    public ErrorType Type { get; set; } = type;
    public HttpStatusCode StatusCode { get; set; } = statusCode;

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
