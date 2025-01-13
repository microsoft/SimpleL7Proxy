using System.Net;

namespace SimpleL7Proxy.Proxy;
// This class represents the data returned from the downstream host.
public class ProxyData
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public WebHeaderCollection Headers { get; set; } = [];

    public WebHeaderCollection ContentHeaders { get; set; } = [];

    public HttpResponseMessage ResponseMessage { get; set; } = new();

    public byte[]? Body { get; set; }

    public string FullURL { get; set; } = string.Empty;

    // this is a copy of the calculated average latency
    public double CalculatedHostLatency { get; set; }

    public string BackendHostname { get; set; } = string.Empty;

    public DateTime ResponseDate { get; set; } = DateTime.UtcNow;
}
