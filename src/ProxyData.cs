using System.Net;


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
    public bool IsStreaming { get; set; } = false;

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
        //BodyStream?.Dispose();
        //BodyStream = null;
        BodyResponseMessage?.Dispose();
        BodyResponseMessage = null;
        Headers?.Clear();
        ContentHeaders?.Clear();
    }
}