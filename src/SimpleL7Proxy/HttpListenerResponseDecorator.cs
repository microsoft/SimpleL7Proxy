using System.Net;

namespace SimpleL7Proxy;

public class HttpListenerResponseDecorator(HttpListenerResponse response)
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

    public WebHeaderCollection Headers => response.Headers;

    public Stream OutputStream => response.OutputStream;
}
