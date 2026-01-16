using System.Net;
using System.Text;

namespace SimpleL7Proxy.Proxy;
// This class represents the data returned from the downstream host.
public class ProxyData : IDisposable
{
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    public WebHeaderCollection Headers { get; set; } = [];
    public WebHeaderCollection ContentHeaders { get; set; } = [];
    public byte[]? Body { get; set; }
    public HttpResponseMessage? BodyResponseMessage { get; set; } = null;

    public string FullURL { get; set; } = string.Empty;

    // this is a copy of the calculated average latency
    public double CalculatedHostLatency { get; set; } = 0;

    public string BackendHostname { get; set; } = string.Empty;

    public DateTime ResponseDate { get; set; } = DateTime.UtcNow;
    public bool IsStreamingResponse { get; set; } = false;
    public string StreamingProcessor { get; set; } = string.Empty;

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("ProxyData: ");
        sb.AppendLine();
        sb.Append($"StatusCode: {StatusCode}, ContentLength: {Body?.Length ?? 0}, BackendHostname: {BackendHostname}, CalculatedHostLatency: {CalculatedHostLatency}");
        sb.Append(" Headers: [");
        foreach (string key in Headers.AllKeys)
        {
            sb.Append($"{key}: {Headers[key]}, ");  
        }
        sb.Append("] ContentHeaders: [");
        foreach (string key in ContentHeaders.AllKeys)
        {
            sb.Append($"{key}: {ContentHeaders[key]}, ");   
        }
        sb.Append("]");
        sb.AppendLine();
        sb.Append($"FullURL: {FullURL}");   
        sb.AppendLine();
        sb.Append($"ResponseDate: {ResponseDate}, IsStreamingResponse: {IsStreamingResponse}, StreamingProcessor: {StreamingProcessor}");
        sb.AppendLine();
        sb.Append($"Body Details: ");
        if (BodyResponseMessage == null)
        {
            sb.Append("BodyResponseMessage is null");
        }
        else
        {
            sb.Append($"BodyResponseMessage StatusCode: {BodyResponseMessage.StatusCode}, ContentLength: {BodyResponseMessage.Content?.Headers.ContentLength ?? 0}");
        }
        return sb.ToString();
    }

    public ProxyData()
    {
        Headers = new WebHeaderCollection();
        ContentHeaders = new WebHeaderCollection();
        FullURL = "";
        BackendHostname = "";
        ResponseDate = DateTime.UtcNow;
    }

    public void Dispose()
    {
        Body = null; // Release large byte array immediately
        BodyResponseMessage?.Dispose();
        BodyResponseMessage = null;
        Headers?.Clear();
        ContentHeaders?.Clear();
    }
}