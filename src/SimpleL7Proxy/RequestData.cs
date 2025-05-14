using System;
using System.IO;
using System.Net;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Backend;

// This class represents the request received from the upstream client.

public class RequestData : IDisposable, IAsyncDisposable
{
    // Static variable to hold the IServiceBusRequestService instance
    public static IServiceBusRequestService? SBRequestService { get; private set; }

    // Method to initialize the static variable from DI
    public static void InitializeServiceBusRequestService(IServiceBusRequestService serviceBusRequestService )
    {
        if (SBRequestService == null)
        {
            SBRequestService = serviceBusRequestService;
        }
    }
    
    public HttpListenerContext? Context { get; private set; }
    public Stream OutputStream {get; set;}
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
    public int Timeout {get; set;}

    // Header timeout or default timeout
    public int defaultTimeout { get; set; } = 0;
    public bool runAsync {get; set; } = false;
    public AsyncWorker? asyncWorker { get; set; } = null;

    public string ExpireReason { get; set; } = "";

    public string TTL="";
    public string  SBClientID { get; set; } = "";
    public ServiceBusMessageStatusEnum SBStatus
    {
        get => _sbStatus;
        set
        {
            _sbStatus = value;
            //SBRequestService?.updateStatus(this);
        }
    }
    private ServiceBusMessageStatusEnum _sbStatus = ServiceBusMessageStatusEnum.None;

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
        OutputStream = context.Response.OutputStream;
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

    // Updates the ExpiresAt time based on the TTL header
     public void CalculateExpiration(int defaultTTLSecs)
    {
        //Console.WriteLine($"Calculating TTL for {request.Headers["S7PTTL"]} {request.TTLSeconds}");

        const string TtlHeaderName = "S7PTTL";
        string ttlString = Headers.Get(TtlHeaderName)!;

        if (!string.IsNullOrWhiteSpace(ttlString))
        {
            ExpireReason = "TTL Header: " + ttlString;

            // TTL can be specified as +300 ( 300 seconds from now ) or as an absolute number of seconds
            if (ttlString.StartsWith('+') && long.TryParse(ttlString.Substring(1), out long relativeSeconds))
            {
                ExpiresAt = EnqueueTime.AddSeconds(relativeSeconds);
            }
            else if (long.TryParse(ttlString, out long absoluteSeconds))
            {
                // Absolute TTL in seconds
                ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(absoluteSeconds).UtcDateTime;

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
            OutputStream?.Flush();
            OutputStream?.Close();
            Context = null;
            Console.WriteLine($"RequestData disposed: {Guid}");
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