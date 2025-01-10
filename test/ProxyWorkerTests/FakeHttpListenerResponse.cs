using System.Net;

namespace Tests;

public class FakeHttpListenerResponse : IHttpListenerResponse
{
    public int StatusCode { get; set; } = 200;
    public bool KeepAlive { get; set; } = false;
    public long ContentLength64 { get; set; } = 1024;
    public WebHeaderCollection Headers { get; } = [];
    public Stream OutputStream { get; } = new MemoryStream();
}