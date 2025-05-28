using System.Net;


// This class represents the data returned from the downstream host.
public class ProxyData
{
    public HttpStatusCode StatusCode { get; set; }
    public WebHeaderCollection Headers { get; set; }
    public WebHeaderCollection ContentHeaders { get; set; }
    public byte[]? Body { get; set; }

    public string FullURL { get; set; }

    // this is a copy of the calculated average latency
    public double CalculatedHostLatency { get; set; } = 0;
    public string BackendHostname { get; set; }

    public DateTime ResponseDate { get; set; }

    public ProxyData()
    {
        Headers = new WebHeaderCollection();
        ContentHeaders = new WebHeaderCollection();
        FullURL = "";
        BackendHostname = "";
        ResponseDate = DateTime.UtcNow;
    }
}