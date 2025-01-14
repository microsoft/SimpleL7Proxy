using System.Net;

namespace SimpleL7Proxy.Proxy;

internal class HttpListenerResponseWrapper(HttpListenerResponse response)
    : IHttpListenerResponse
{
    public int StatusCode
    {
        get => response.StatusCode;
        set => response.StatusCode = value;
    }

    public bool KeepAlive
    {
        get => response.KeepAlive;
        set => response.KeepAlive = value;
    }

    public long ContentLength64
    {
        get => response.ContentLength64;
        set => response.ContentLength64 = value;
    }

    public WebHeaderCollection Headers
    {
        get => response.Headers;
        set => response.Headers = value;
    }

    public Stream OutputStream => response.OutputStream;
}
