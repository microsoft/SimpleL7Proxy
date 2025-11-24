using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using Shared.RequestAPI.Models;
using SimpleL7Proxy.BackupAPI;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Feeder;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.User;


// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow
// This class represents the request received from the upstream client.
public class RequestData : IDisposable, IAsyncDisposable  
{
    // Static variable to hold the IServiceBusRequestService instance
    public static IServiceBusRequestService? SBRequestService { get; private set; }
    public static IBackupAPIService? BackupAPIService { get; private set; }
    public static IUserPriorityService? UserPriorityService { get; private set; }
    public static BackendOptions? BackendOptionsStatic { get; private set; }

    // -- ASYNC RELATED PARAMS --

    private ServiceBusMessageStatusEnum _sbStatus = ServiceBusMessageStatusEnum.None;
    public int AsyncBlobAccessTimeoutSecs { get; set; } = 3600; // 1 hour
    public AsyncWorker? asyncWorker { get; set; } = null;
    public bool AsyncTriggered { get; set; } = false;
    public bool AsyncHydrated { get; set; } = false;
    public string BlobContainerName { get; set; } = "";
    public bool runAsync { get; set; } = false;
    public string SBTopicName { get; set; } = "";

    private string _backgroundRequestId = "";
    public string BackgroundRequestId
    {
        get => _backgroundRequestId;
        set {
            _backgroundRequestId = value;
            _requestAPIDocument ??= RequestDataConverter.ToRequestAPIDocument(this);
            _requestAPIDocument.backgroundRequestId = value;

        }
    }

    private bool _isBackground = false;
    public bool IsBackground
    {
        get => _isBackground;
        set
        {
            _isBackground = value;
            _requestAPIDocument ??= RequestDataConverter.ToRequestAPIDocument(this);
            _requestAPIDocument.isBackground = value;
        }
    }
    public bool IsBackgroundCheck { get; set; } = false;
    public bool BackgroundRequestCompleted { get; set; } = false;
    
    /// <summary>
    /// Computed request type based on async flags and background state.
    /// Determines processing mode: Sync, Async, AsyncBackground, or AsyncBackgroundCheck.
    /// </summary>
    public RequestType Type
    {
        get
        {
            if (!runAsync)
            {
                return RequestType.Sync;
            }

            if (IsBackgroundCheck)
            {
                return RequestType.AsyncBackgroundCheck;
            }

            if (IsBackground)
            {
                return RequestType.AsyncBackground;
            }

            return RequestType.Async;
        }
    }
    
    public RequestAPIDocument? _requestAPIDocument; // For tracking async and background status updates
    public RequestAPIStatusEnum RequestAPIStatus
    {
        get => _requestAPIDocument?.status ?? RequestAPIStatusEnum.New;
        set
        {
            _requestAPIDocument ??= RequestDataConverter.ToRequestAPIDocument(this);

            if (_requestAPIDocument != null)
            {
                _requestAPIDocument.status = value;
                if (runAsync)
                {
                    BackupAPIService!.UpdateStatus(_requestAPIDocument);
                }
            }
        }
    }

    public ServiceBusMessageStatusEnum SBStatus
    {
        get => _sbStatus;
        set
        {
            _sbStatus = value;
            if (runAsync)
            {
                SBRequestService?.updateStatus(this);
            }
        }
    }

    public IRequestProcessor? RecoveryProcessor { get; set; } = null;

    // -- END ASYNC RELATED PARAMS --


    // Number of times Proxy calls the backend
    public int BackendAttempts { get; set; } = 0;
    
    // Total attempts including retries by downstream services
    public int TotalDownstreamAttempts { get; set; } = 0; 

    public bool Debug { get; set; }
    public bool SkipDispose { get; set; } = false;
    public byte[]? BodyBytes { get; set; } = null;
    public DateTime DequeueTime { get; set; }
    public DateTime EnqueueTime { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime Timestamp { get; private set; }
    public Guid Guid { get; set; }
    public HttpListenerContext? Context { get; private set; }
    public int defaultTimeout { get; set; } = 0; // header timeout or default timeout in milliseconds
    public int Priority { get; set; }
    public int Priority2 { get; set; }
    public int Timeout { get; set; }  // calculated timeout in milliseconds
    public List<Dictionary<string, string>> incompleteRequests = new();
    public ProxyEvent EventData = new();
    public Stream? OutputStream { get; set; }
    public Stream? Body { get; private set; }
    public string ExpireReason { get; set; } = "";
    public string ExpiresAtString { get; set; } = "";
    public string FullURL { get; set; }
    public string Method { get; set; }
    public string MID { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string Path { get; set; }
    public bool Requeued { get; set; } = false;
    public string UserID { get; set; } = "";
    public string profileUserId { get; set; } = "";
    public WebHeaderCollection Headers { get; private set; }

    // Method to initialize the static variable from DI
    public static void InitializeServiceBusRequestService(IServiceBusRequestService serviceBusRequestService,
                                                          IBackupAPIService backupAPIService,
                                                          IUserPriorityService userPriorityService,
                                                          BackendOptions backendOptions)
    {
        SBRequestService ??= serviceBusRequestService;
        BackupAPIService ??= backupAPIService;
        UserPriorityService ??= userPriorityService;
        BackendOptionsStatic ??= backendOptions;
    }

    public RequestData(string id, Guid guid, string mid, string path, string method, DateTime enqueueTime, Dictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentNullException("RequestData");
        }

        Path = path;
        Timestamp = enqueueTime;
        EnqueueTime = enqueueTime;
        Method = method;
        Headers = new WebHeaderCollection();
        Body = null;
        Context = null;
        ExpiresAt = DateTime.MinValue;  // Set it after reading the headers
        FullURL = "";
        Debug = false;
        MID = mid;
        Guid = guid;

        // restore the headers
        foreach (var kvp in headers)
        {
            Headers[kvp.Key] = kvp.Value;
        }

        // ASYNC
        OutputStream = null; // Will be set when processing the request
    }

    public void Populate(string id, Guid guid, string mid, string path, string method, DateTime enqueueTime, Dictionary<string, string> headers)
    {
        Path = path;
        Timestamp = enqueueTime;
        EnqueueTime = enqueueTime;
        Method = method;
        Headers = new WebHeaderCollection();
        Context = null;
        FullURL = "";
        Debug = false;
        MID = mid;
        Guid = guid;

        // restore the headers
        foreach (var kvp in headers)
        {
            Headers[kvp.Key] = kvp.Value;
        }

        // ASYNC
        OutputStream = null; // Will be set when processing the request
    }

    public void setBody(byte[] bytes)
    {
        BodyBytes = bytes;
        Body = new MemoryStream(bytes);
    }

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

        // ASYNC
        try
        {
            // Initialize the OutputStream with the context's response output stream
            //OutputStream = new AsyncOutputStream(context.Response.OutputStream);
            OutputStream = context.Response.OutputStream;

        }
        catch (Exception ex)
        {
            throw new IOException("Failed to initialize OutputStream for the request.", ex);
        }
    }

    public async Task<byte[]> CacheBodyAsync()
    {

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

    public void CalculateExpiration(int defaultTTLSecs, string TtlHeaderName)
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
            else if (float.TryParse(ttlString, out float relativeSeconds))
            {
                // Relative TTL in seconds ( e.g.  2.5s => 2500 ms)
                ExpiresAt = EnqueueTime.AddMilliseconds(relativeSeconds * 1000);
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

        ExpiresAtString = ExpiresAt.ToString("o");
    }

    public void Cleanup()
    {
        EventData.SendEvent();

        if (BackendOptionsStatic?.UseProfiles == true)
        {
            UserPriorityService?.removeRequest(UserID, Guid.NewGuid());
        }
    }

    // Implement IDisposable
    public void Dispose()
    {

        if (SkipDispose)
        {
            //Console.WriteLine("RequestData: Dispose called but SkipDispose is true. ----------------");
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

            try
            {
                Body?.Dispose();
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose of request body stream.");
            }
            finally
            {
                Body = null;
            }

            try
            {
                Context?.Request?.InputStream?.Dispose();
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose of request input stream.");
            }

            try
            {
                Context?.Response?.OutputStream?.Dispose();
            }
            catch (Exception)
            {
                // Ignore exceptions during dispose
            }

            try
            {
                Context?.Response?.Close();
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to close response context.");
            }

            try
            {
                if (OutputStream != null && OutputStream != Context?.Response?.OutputStream)
                {
                    OutputStream.Flush();
                    OutputStream.Dispose();
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore if the stream was already disposed
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose output stream.");
            }
            finally
            {
                OutputStream = null;
            }

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

        GC.SuppressFinalize(this);
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (SkipDispose)
        {
            return;
        }

        // Always dispose AsyncWorker if it was created
        if (asyncWorker != null)
        {
            try
            {
                await asyncWorker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to dispose AsyncWorker: {ex.Message}");
            }
            finally
            {
                asyncWorker = null;
            }
        }

        // Dispose Body stream
        if (Body != null)
        {
            try
            {
                await Body.DisposeAsync();
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose of request body stream.");
            }
            finally
            {
                Body = null;
            }
        }

        // Dispose Context streams
        if (Context != null)
        {
            try
            {
                if (Context.Request?.InputStream != null)
                {
                    await Context.Request.InputStream.DisposeAsync();
                }
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose of request input stream.");
            }

            try
            {
                if (Context.Response?.OutputStream != null)
                {
                    await Context.Response.OutputStream.DisposeAsync();
                }
            }
            catch (Exception)
            {
                // Ignore exceptions during dispose
            }

            try
            {
                Context.Response?.Close();
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to close response context.");
            }

            Context = null;
        }

        // Dispose OutputStream if different from Context.Response.OutputStream
        if (OutputStream != null && OutputStream != Context?.Response?.OutputStream)
        {
            try
            {
                await OutputStream.FlushAsync();
                await OutputStream.DisposeAsync();
            }
            catch (ObjectDisposedException)
            {
                // Ignore if the stream was already disposed
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to dispose output stream.");
            }
            finally
            {
                OutputStream = null;
            }
        }
    }

    // Destructor to ensure resources are released
    ~RequestData()
    {
        Dispose(false);
    }
}