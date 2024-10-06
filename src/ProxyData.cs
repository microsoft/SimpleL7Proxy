using System.Net;


// This class represents the data returned from the downstream host.
public class ProxyData
{
    public HttpStatusCode StatusCode { get; set; }
    public WebHeaderCollection Headers { get; set; }
    public WebHeaderCollection ContentHeaders { get; set; }
    public byte[]? Body { get; set; }

    public string FullURL { get; set; }

    public DateTime ResponseDate { get; set; }

    public ProxyData()
    {
        Headers = new WebHeaderCollection();
        ContentHeaders = new WebHeaderCollection();
        FullURL="";
    }
}