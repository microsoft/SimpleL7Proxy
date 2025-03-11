using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Amqp;

// This class represents the request received from the upstream client.

public class RequestData : IDisposable, IAsyncDisposable
{
    public HttpListenerContext? Context { get; private set; }
    public Stream? Body { get; private set; }
    public DateTime Timestamp { get; private set; }
    public string Path { get; private set; }
    public string Method { get; private set; }
    public WebHeaderCollection Headers { get; private set; }
    public string FullURL { get;  set; }
    public bool Debug { get; set; } 

    public byte[]? BodyBytes { get; set; }=null;
    public int Priority { get; set; }
    public int Priority2 { get; set; }
    public DateTime EnqueueTime { get; set; }
    public DateTime DequeueTime { get; set; }
    public string MID { get; set; } = "";
    public Guid Guid { get; set; }
    public string UserID { get; set; } = "";
    public int Timeout {get; set;}

    public string TTL="";
    public long TTLSeconds = 0;

    // Track if the request was re-qued for cleanup purposes
    //public bool Requeued { get; set; } = false;
    public bool SkipDispose { get; set; } = false;

    public RequestData(HttpListenerContext context, string mid)
    {
        if (context.Request.Url?.PathAndQuery == null)
        {
            throw new ArgumentNullException("RequestData");
        }

        Path = context.Request.Url.PathAndQuery;
        Method = context.Request.HttpMethod;
        Headers = (WebHeaderCollection)context.Request.Headers;
        Body = context.Request.InputStream;
        Context = context;
        Timestamp = DateTime.UtcNow;
        FullURL = "";
        Debug = false;
        MID = mid;
    }

    public async Task<byte[]> CachBodyAsync() {

        if (BodyBytes != null)
        {
            return BodyBytes;
        }

        if (Body is null)
        {
            return [];
        }

        // Read the body stream once and reuse it
        using (MemoryStream ms = new MemoryStream())
        {
            await Body.CopyToAsync(ms);
            BodyBytes = ms.ToArray();
        }

        return BodyBytes;
    }

    // Implement IDisposable
    public void Dispose()
    {

        if (SkipDispose)
        {
            return;
        }

        Dispose(true);
        GC.SuppressFinalize(this);
    
    }

    protected virtual void Dispose(bool disposing)
    {
        if (SkipDispose)
        {
            return;
        }

        if (disposing)
        {
            // Dispose managed resources
            FullURL = Path = Method = "";
            Body?.Dispose();
            Body = null;
            Context?.Request?.InputStream?.Dispose();
            Context?.Response?.OutputStream?.Dispose();
            Context?.Response?.Close();
            Context = null;
        }
    }

    // Implement IAsyncDisposable
    public async ValueTask DisposeAsync()
    {
        if (SkipDispose)
        {
            return;
        }

        await DisposeAsyncCore();

        // Dispose of unmanaged resources
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {


        if (SkipDispose)
        {
            return;
        }

        if (Body != null)
        {
            await Body.DisposeAsync();
            Body = null;
        }

        if (Context != null)
        {
            if (Context.Request?.InputStream != null)
            {
                await Context.Request.InputStream.DisposeAsync();
            }

            try 
            {
                if (Context?.Response?.OutputStream != null)
                {
                    await Context.Response.OutputStream.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // Ignore exceptions
            }

            Context?.Response?.Close();
            Context = null;
        }
    }

    // Destructor to ensure resources are released
    ~RequestData()
    {
        Dispose(false);
    }
}