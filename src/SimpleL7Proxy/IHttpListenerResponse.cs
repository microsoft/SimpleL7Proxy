using System.Net;

namespace SimpleL7Proxy;

public interface IHttpListenerResponse
{
    int StatusCode { get; set; }
    bool KeepAlive { get; set; }
    long ContentLength64 { get; set; }
    WebHeaderCollection Headers { get; }
    Stream OutputStream { get; }
}
