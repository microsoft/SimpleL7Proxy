using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Amqp;

// This class represents the request received from the upstream client.

public class RequestData : IDisposable, IAsyncDisposable
{
    public int Attempts { get; set; } = 0;
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
    public DateTime ExpiresAt { get; set; }
    public string ExpiresAtString { get; set; } = "";
    public string MID { get; set; } = "";
    public Guid Guid { get; set; }
    public string UserID { get; set; } = "";

    // calculated timeout
    public int Timeout { get; set; }

    // Header timeout or default timeout
    public int defaultTimeout { get; set; } = 0;
    public string ExpireReason { get; set; } = "";
    public ProxyEvent EventData = new ();
    public List<Dictionary<string, string>> incompleteRequests = new();

    //public string TTL = "";

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
        ExpiresAt = DateTime.MinValue;  // Set it after reading the headers
        FullURL = "";
        Debug = false;
        MID = mid;
    }

    public async Task<byte[]> CacheBodyAsync() {

        if (BodyBytes != null)
        {
            return BodyBytes;
        }

        if (Body is null)
        {
            return [];
        }

        // Read the body stream once and reuse it
        using (MemoryStream ms = new())
        {
            await Body.CopyToAsync(ms);
            BodyBytes = ms.ToArray();
        }

        return BodyBytes;
    }

    public void CalculateExpiration(int defaultTTLSecs, string TtlHeaderName )
    {
        //Console.WriteLine($"Calculating TTL for {request.Headers["S7PTTL"]} {request.TTLSeconds}");

        string ttlString = Headers.Get(TtlHeaderName)!;

        if (!string.IsNullOrWhiteSpace(ttlString))
        {
            ExpireReason = "TTL Header: " + ttlString;

            // TTL can be specified as 300 ( 300 seconds from now ) or +nnnn as an absolute number of seconds
            if (ttlString.StartsWith('+') && long.TryParse(ttlString.Substring(1), out long absoluteSeconds))
            {
                // Absolute TTL in seconds
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(absoluteSeconds).UtcDateTime;
            }
            else if (float.TryParse(ttlString, out float relativeSeconds ))
            {
                // Relative TTL in seconds ( e.g.  2.5s => 2500 ms)
                ExpiresAt = EnqueueTime.AddMilliseconds(relativeSeconds * 1000 );
            }
            else if (DateTimeOffset.TryParse(ttlString, out var ttlOffset))
            {
                // If the offset is zero, treat as UTC; otherwise, convert to UTC
                if (ttlOffset.Offset == TimeSpan.Zero)
                {
                    ExpiresAt = ttlOffset.UtcDateTime;
                }
                else
                {
                    // Handles cases where the string specifies a local time or a non-UTC offset
                    ExpiresAt = ttlOffset.ToUniversalTime().UtcDateTime;
                }
            }
            else 
            {
                throw new ProxyErrorException(ProxyErrorException.ErrorType.InvalidTTL,
                                              HttpStatusCode.BadRequest,
                                              $"Invalid TTL format: '{ttlString}'");
            }
        }
        else
        {
            int ttlMsToUse = (defaultTTLSecs > 0) ? defaultTTLSecs * 1000 : defaultTimeout;
            ExpireReason = (defaultTTLSecs > 0) ? $"Default TTL: {defaultTTLSecs} secs" : $"Default Timeout: {defaultTimeout} ms";
            ExpiresAt = EnqueueTime.AddMilliseconds(ttlMsToUse);
        }
        
        ExpiresAtString = ExpiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ");
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