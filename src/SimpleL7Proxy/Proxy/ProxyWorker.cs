using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;

namespace SimpleL7Proxy.Proxy;

// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker 
{

    private int PreferredPriority;
    private readonly CancellationToken _cancellationToken;
    private static bool _debug = false;
    private static IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient;
    private readonly IEventClient _eventClient;
    private readonly ILogger<ProxyWorker> _logger;
    private readonly ProxyStreamWriter _proxyStreamWriter;
    private IUserPriorityService _userPriority;
    private IUserProfileService _profiles;
    private string IDstr = "";
    public static int activeWorkers = 0;
    private static bool readyToWork = false;

    private static int[] states = [0, 0, 0, 0, 0, 0, 0, 0];

    public static string GetState()
    {
        return $"Count: {activeWorkers} QLen: {_requestsQueue?.thrdSafeCount.ToString() ?? "-"} States: [ deq-{states[0]} pre-{states[1]} prxy-{states[2]} -[snd-{states[3]} rcv-{states[4]}]-  wr-{states[5]} rpt-{states[6]} cln-{states[7]} ]";
    }
    //public ProxyWorker(CancellationToken cancellationToken, int ID, int priority, ConcurrentPriQueue<RequestData> requestsQueue, BackendOptions backendOptions, IUserPriority? userPriority, IUserProfile? profiles, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient)
    public ProxyWorker(
        int ID,
        int priority,
        IConcurrentPriQueue<RequestData> requestsQueue, 
        BackendOptions backendOptions, 
        IBackendService? backends, 
        IUserProfileService? profiles,
        IUserPriorityService? userPriority,
        IEventClient eventClient, 
        TelemetryClient? telemetryClient,
        ILogger<ProxyWorker> logger,
        ProxyStreamWriter proxyStreamWriter,
        CancellationToken cancellationToken)
        {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventClient = eventClient;
        _logger = logger;
        _proxyStreamWriter = proxyStreamWriter;
        //_eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _userPriority = userPriority ?? throw new ArgumentNullException(nameof(userPriority));
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        IDstr = ID.ToString();
        PreferredPriority = priority;
    }

    public async Task TaskRunner()
    {
        bool doUserconfig = _options.UseProfiles;
        string workerState = "";


        if (doUserconfig && _profiles == null) throw new ArgumentNullException(nameof(_profiles));
        if (_requestsQueue == null) throw new ArgumentNullException(nameof(_requestsQueue));

        // Only for use during shutdown after graceseconds have expired
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        // CancellationTokenSource workerCancelTokenSource = new CancellationTokenSource();
        // var workerCancelToken = workerCancelTokenSource.Token;

        // increment the active workers count
        Interlocked.Increment(ref activeWorkers);
        if (_options.Workers == activeWorkers)
        {
            readyToWork = true;
            _logger.LogInformation("All workers ready to work");
        }

        // Stop the worker if the cancellation token is cancelled and there is no work to do
        while (!_cancellationToken.IsCancellationRequested || _requestsQueue.thrdSafeCount > 0)
        {
            RequestData incomingRequest;
            try
            {
                Interlocked.Increment(ref states[0]);
                workerState = "Waiting";

                // This will block until an item is available or the token is cancelled
                incomingRequest = await _requestsQueue.DequeueAsync(PreferredPriority).ConfigureAwait(false);
                if (incomingRequest == null)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break; // Exit the loop if the operation is cancelled
            }
            finally
            {
                Interlocked.Decrement(ref states[0]);
                workerState = "Exit - Get Work";
            }

            incomingRequest.DequeueTime = DateTime.UtcNow;

            await using (incomingRequest)
            {
                var requestWasRetried = false;
                var lcontext = incomingRequest?.Context;
                bool isExpired = false;

                Interlocked.Increment(ref states[1]);
                workerState = "Processing";

                ProxyEvent proxyEvent = new()
                {
                    EventData = {
                        ["PoxyHost"] = _options.HostName,
                        ["Date"] = incomingRequest!.DequeueTime.ToString("o") ?? DateTime.UtcNow.ToString("o"),
                        ["Path"] = incomingRequest.Path ?? "N/A",
                        ["x-RequestPriority"] = incomingRequest.Priority.ToString() ?? "N/A",
                        ["x-RequestMethod"] = incomingRequest.Method ?? "N/A",
                        ["x-RequestPath"] = incomingRequest.Path ?? "N/A",
                        ["x-RequestHost"] = incomingRequest.Headers["Host"] ?? "N/A",
                        ["x-RequestUserAgent"] = incomingRequest.Headers["User-Agent"] ?? "N/A",
                        ["x-RequestContentType"] = incomingRequest.Headers["Content-Type"] ?? "N/A",
                        ["x-RequestContentLength"] = incomingRequest.Headers["Content-Length"] ?? "N/A",
                        ["x-RequestWorker"] = IDstr
                    }
                };
                var proxyEventData = proxyEvent.EventData;

                if (lcontext == null || incomingRequest == null)
                {
                    Interlocked.Decrement(ref states[1]);
                    workerState = "Exit - No Context";
                    Interlocked.Increment(ref states[7]);
                    continue;
                }
//                var lcontextResponse = lcontext.Response;
//                var lcontextResponseHeaders = lcontextResponse.Headers;
//                var outputStream = lcontextResponse.OutputStream;
                bool dirtyExceptionLog = false;
                bool requestException = false;
                try
                {
                    if (Constants.probes.Contains(incomingRequest.Path))
                    {
                        ProbeResponse(incomingRequest.Path!, out int probeStatus, out string probeMessage);

                        lcontext.Response.StatusCode = probeStatus;
                        lcontext.Response.ContentType = "text/plain";
                        lcontext.Response.Headers.Add("Cache-Control", "no-cache");
                        lcontext.Response.KeepAlive = false;

                        var healthMessage = Encoding.UTF8.GetBytes(probeMessage);
                        lcontext.Response.ContentLength64 = healthMessage.Length;

                        await lcontext.Response.OutputStream.WriteAsync(
                            healthMessage,
                            0,
                            healthMessage.Length).ConfigureAwait(false);
                            
                        Interlocked.Decrement(ref states[1]);
                        workerState = "Exit - Probe";
                        Interlocked.Increment(ref states[7]);

                        if (_options.LogProbes)
                        {
                            proxyEventData["Probe"] = incomingRequest.Path!;
                            proxyEventData["ProbeStatus"] = probeStatus.ToString();
                            proxyEventData["ProbeMessage"] = probeMessage;
                            _eventClient?.SendData(proxyEvent);
                            _logger.LogInformation($"Probe: {incomingRequest.Path} Status: {probeStatus} Message: {probeMessage}");
                        }
                        continue;
                    }



                    incomingRequest.Headers["x-Request-Queue-Duration"] = (incomingRequest.DequeueTime! - incomingRequest.EnqueueTime!).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Process-Duration"] = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Worker"] = IDstr;
                    incomingRequest.Headers["x-S7PID"] = incomingRequest.MID ?? "N/A";
                    incomingRequest.Headers["x-S7PPriority"] = incomingRequest.Priority.ToString() ?? "N/A";

                    proxyEventData["x-Request-Queue-Duration"] = incomingRequest.Headers["x-Request-Queue-Duration"] ?? "N/A";
                    proxyEventData["x-Request-Process-Duration"] = incomingRequest.Headers["x-Request-Process-Duration"] ?? "N/A";
                    proxyEventData["x-S7PID"] = incomingRequest.MID ?? "N/A";

                    Interlocked.Decrement(ref states[1]);
                    Interlocked.Increment(ref states[2]);
                    workerState = "Read Proxy";

                    //  FIND A BACKEND AND SEND THE REQUEST

                    var pr = await ProxyToBackEndAsync(incomingRequest, token).ConfigureAwait(false);

                    // POST PROCESSING ... logging
                    Interlocked.Decrement(ref states[2]);
                    Interlocked.Increment(ref states[5]);
                    workerState = "Write Response";

                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run

                    proxyEventData["x-Status"] = ((int)pr.StatusCode).ToString();
                    if (_options.LogHeaders?.Count > 0)
                    {
                        foreach (var header in _options.LogHeaders)
                        {
                            proxyEventData[header] = pr.Headers[header] ?? "N/A";
                        }
                    }

                    if (pr.StatusCode == HttpStatusCode.Gone) // 410 Gone
                    {
                        // Request has expired
                        isExpired = true;
                    }

                    //await WriteDataToStreamAsync(lcontext, pr, token).ConfigureAwait(false);
                    //                    Task.Yield(); // Yield to the scheduler to allow other tasks to run
                    Interlocked.Decrement(ref states[5]);
                    Interlocked.Increment(ref states[6]);
                    workerState = "Send Event";

                    var conlen = pr.ContentHeaders?["Content-Length"] ?? "N/A";
                    var proxyTime = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    var timeTaken = (DateTime.UtcNow - incomingRequest.EnqueueTime).TotalMilliseconds.ToString("F3");
                    _logger.LogInformation($"Pri: {incomingRequest.Priority} Stat: {(int)pr.StatusCode} Len: {conlen} {pr.FullURL} Deq: {incomingRequest.DequeueTime} Lat: {proxyTime} ms");

                    proxyEventData["Url"] = pr.FullURL;
                    proxyEventData["x-Response-Latency"] = (pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    proxyEventData["x-Total-Latency"] = timeTaken;
                    proxyEventData["x-Backend-Host"] = pr?.BackendHostname ?? "N/A";
                    proxyEventData["x-Backend-Host-Latency"] = pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
                    proxyEventData["Content-Length"] = lcontext.Response?.ContentLength64.ToString() ?? "N/A";
                    proxyEventData["Content-Type"] = lcontext?.Response?.ContentType ?? "N/A";
                    workerState = "Send Event";

                    if (_eventClient != null)
                    {
                        proxyEventData["Type"] = isExpired ? "S7P-Expired-Request" : "S7P-ProxyRequest";
                        _eventClient.SendData(proxyEvent);
                    }

                    // pr.Body = [];
                    // pr.ContentHeaders.Clear();
                    // pr.FullURL = "";
                    Interlocked.Decrement(ref states[6]);
                    Interlocked.Increment(ref states[7]);
                    workerState = "Cleanup";
                }
                catch (ProxyErrorException e) {
                    _logger.LogInformation($"{e.Message}.  Pri: {incomingRequest.Priority} Queue Length: {_requestsQueue.thrdSafeCount} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}");

                    if (e.Type == ProxyErrorException.ErrorType.TTLExpired ) {
                        isExpired = true;
                    }

                    lcontext.Response.StatusCode = (int)e.StatusCode;
                    var errorMessage = Encoding.UTF8.GetBytes(e.Message);
                    try
                    {
                        await lcontext.Response.OutputStream.WriteAsync(
                            errorMessage, 
                            0, 
                            errorMessage.Length).ConfigureAwait(false);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError($"Failed to write error message: {writeEx.Message}");
                        proxyEventData["x-Status"] = "Network Error";
                    }

                    var ResponseDate = DateTime.UtcNow;

                    proxyEventData["Url"] = incomingRequest.FullURL;
                    proxyEventData["x-Status"] = ((int)503).ToString();
                    proxyEventData["x-Response-Latency"] = (ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    proxyEventData["x-Total-Latency"] = (DateTime.UtcNow - incomingRequest.EnqueueTime).TotalMilliseconds.ToString("F3");
                    proxyEventData["x-Backend-Host"] = "N/A";
                    proxyEventData["x-Backend-Host-Latency"] = "N/A";
                    proxyEventData["Type"] = "S7P-Not-Processed";
                    _eventClient.SendData(proxyEvent);
                }
                catch (S7PRequeueException e)
                {
                    // Requeue the request 
                    _requestsQueue.Requeue(incomingRequest, incomingRequest.Priority, incomingRequest.Priority2, incomingRequest.EnqueueTime);
                    _logger.LogInformation($"Requeued request.  Pri: {incomingRequest.Priority} Queue Length: {_requestsQueue.thrdSafeCount} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}");
                    requestWasRetried = true;
                    incomingRequest.SkipDispose = true;
                    proxyEventData["Url"] = e.pr.FullURL;
                    proxyEventData["x-Status"] = ((int)503).ToString();
                    proxyEventData["x-Response-Latency"] = (e.pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    proxyEventData["x-Total-Latency"] = (DateTime.UtcNow - incomingRequest.EnqueueTime).TotalMilliseconds.ToString("F3");
                    proxyEventData["x-Backend-Host"] = e.pr?.BackendHostname ?? "N/A";
                    proxyEventData["x-Backend-Host-Latency"] = e.pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
                    proxyEventData["Type"] = "S7P-Requeue-Request";
                    _eventClient.SendData(proxyEvent);

                }
                catch (IOException ioEx)
                {
                    if (isExpired)
                    {
                        _logger.LogError("IoException on an exipred request");
                        continue;
                    }
                    proxyEventData["x-Status"] = "408";
                    proxyEventData["Type"] = "S7P-IOException";
                    proxyEventData["x-Message"] = ioEx.Message;

                    _logger.LogError($"An IO exception occurred: {ioEx.Message}");
                    lcontext.Response.StatusCode = 408;
                    var errorMessage = Encoding.UTF8.GetBytes($"Broken Pipe: {ioEx.Message}");
                    try 
                    {
                        await lcontext.Response.OutputStream.WriteAsync(
                            errorMessage, 
                            0, 
                            errorMessage.Length).ConfigureAwait(false);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError($"Failed to write error message: {writeEx.Message}");
                        proxyEventData["x-Status"] = "Network Error";
                    }

                    _eventClient.SendData(proxyEvent);

                }
                catch (Exception ex)
                {
                    if (isExpired)
                    {
                        _logger.LogError("Exception on an exipred request");
                        continue;
                    }

                    proxyEventData["x-Status"] = "500";
                    proxyEventData["Type"] = "S7P-Exception";
                    proxyEventData["x-Message"] = ex.Message;

                    if (ex.Message == "Cannot access a disposed object." 
                        || ex.Message.StartsWith("Unable to write data")
                        || ex.Message.Contains("Broken Pipe")) // The client likely closed the connection
                    {
                        _logger.LogError($"Client closed connection: {incomingRequest.FullURL}");
                        proxyEventData["x-Status"] = "Network Error";
                        _eventClient.SendData(proxyEvent);

                        continue;
                    }
                    requestException = true;
                    dirtyExceptionLog = true;
                    // Log the exception
                    _logger.LogError($"Exception: {ex.Message}");
                    _logger.LogError($"Stack Trace: {ex.StackTrace}");
                    // Convert the multi-line stack trace to a single line
                    proxyEventData["x-Stack"] = ex.StackTrace?.Replace(Environment.NewLine, " ") ?? "N/A";
                    proxyEventData["x-WorkerState"] = workerState;

                    _eventClient.SendData(proxyEvent);
                    dirtyExceptionLog = false;

                    // Set an appropriate status code for the error
                    lcontext.Response.StatusCode = 500;
                    var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
                    try
                    {
                        _telemetryClient?.TrackException(ex, proxyEvent.EventData);
                        await lcontext.Response.OutputStream.WriteAsync(
                                errorMessage, 
                                0, 
                                errorMessage.Length).ConfigureAwait(false);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogError($"Failed to write error message: {writeEx.Message}");
                    }
                }
                finally
                {
                    try {
                        if (dirtyExceptionLog)
                        {
                            proxyEventData["x-Additional"] = "Failed to log exception";
                            _eventClient.SendData(proxyEvent);
                        }
                        // Let's not track the request if it was retried.
                        if (!requestWasRetried)
                        {
                            if (doUserconfig)
                                _userPriority.removeRequest(incomingRequest.UserID, incomingRequest.Guid);

                            // Track the status of the request for circuit breaker
                            _backends.TrackStatus((int)lcontext.Response.StatusCode, requestException);

                            _telemetryClient?.TrackRequest($"{incomingRequest.Method} {incomingRequest.Path}",
                                DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{lcontext.Response.StatusCode}", true);
                            //_telemetryClient?.TrackEvent("ProxyRequest", proxyEvent);

                            lcontext?.Response.Close();
                        } 
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"Error in finally: {e.Message}");
                    }

                    Interlocked.Decrement(ref states[7]);
                    workerState = "Exit - Finally";
                }
            }
        }

        Interlocked.Decrement(ref activeWorkers);

        _logger.LogInformation($"Worker {IDstr} stopped.");
    }


    

    // Method to replace or add a header
    void ReplaceOrAddHeader(WebHeaderCollection headers, string headerName, string headerValue)
    {
        if (headers[headerName] != null)
        {
            headers.Remove(headerName);
        }
        headers.Add(headerName, headerValue);
    }

    private async Task WriteDataToStreamAsync(HttpListenerContext context, 
                                              ProxyData pr,
                                              CancellationToken token)
    {
        HttpListenerResponseWrapper listener = new(context.Response);
        await _proxyStreamWriter.WriteDataToStreamAsync(listener, pr, token);
        // // Set the response status code
        // context.Response.StatusCode = (int)pr.StatusCode;

        // // Copy headers to the response
        // CopyHeadersToResponse(pr.Headers, context.Response.Headers);

        // // Set content-specific headers
        // if (pr.ContentHeaders != null)
        // {
        //     foreach (var key in pr.ContentHeaders.AllKeys)
        //     {
        //         switch (key.ToLower())
        //         {
        //             case "content-length":
        //                 var length = pr.ContentHeaders[key];
        //                 if (long.TryParse(length, out var contentLength))
        //                 {
        //                     context.Response.ContentLength64 = contentLength;
        //                 }
        //                 else
        //                 {
        //                     Console.WriteLine($"Invalid Content-Length: {length}");
        //                 }
        //                 break;

        //             case "content-type":
        //                 context.Response.ContentType = pr.ContentHeaders[key];
        //                 break;

        //             default:
        //                 context.Response.Headers[key] = pr.ContentHeaders[key];
        //                 break;
        //         }
        //     }
        // }

        // context.Response.KeepAlive = false;

        // // foreach (var key in context.Response.Headers.AllKeys)
        // // {
        // //     Console.WriteLine($"Response Header: {key} : {context.Response.Headers[key]}");
        // // }

        // // Write the response body to the client as a byte array
        // if (pr.Body != null)
        // {
        //     await using (var memoryStream = new MemoryStream(pr.Body))
        //     {
        //         await memoryStream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        //         await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
        //     }
        // }
    }

     //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    public async Task<ProxyData> ProxyToBackEndAsync(RequestData request,
                                                CancellationToken token)
    {
        if (request == null) throw new ArgumentNullException(nameof(request), "Request cannot be null.");
        if (request.Body == null) throw new ArgumentNullException(nameof(request.Body), "Request body cannot be null.");
        if (request.Headers == null) throw new ArgumentNullException(nameof(request.Headers), "Request headers cannot be null.");
        if (request.Method == null) throw new ArgumentNullException(nameof(request.Method), "Request method cannot be null.");

        var activeHosts = _backends.GetActiveHosts();
        request.Debug = _debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));
        byte[] bodyBytes = await request.CacheBodyAsync().ConfigureAwait(false);
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;

        // async Task<ProxyData> WriteProxyDataAsync(ProxyData data)
        // {
        //     var context = request.Context
        //         ?? throw new NullReferenceException("Request context cannot be null.");

        //     data.ResponseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        //     await WriteDataToStreamAsync(context, data, token);
        //     return data;
        // }
        // Convert S7PTTL to DateTime
        if (request.TTLSeconds == 0)
        {
            if (!CalculateTTL(request))
            {
                throw new ProxyErrorException(ProxyErrorException.ErrorType.InvalidTTL, 
                                              HttpStatusCode.BadRequest, 
                                              "Invalid TTL format");
                //return await WriteProxyDataAsync(new()
               // {
                //    StatusCode = HttpStatusCode.BadRequest,
                //    ResponseMessage = new(HttpStatusCode.InternalServerError)
                //    {
                //        Content = new StringContent("Invalid TTL format: " + request.Headers["S7PTTL"], Encoding.UTF8)
                //    }
                //});
            }
        }

        if (_options.UseOAuth)
        {
            // Get a token
            var OAToken = _backends.OAuth2Token();
            if (request.Debug)
            {
                _logger.LogDebug("Token: " + OAToken);
            }
            // Set the token in the headers
            request.Headers.Set("Authorization", $"Bearer {OAToken}");
        }

        foreach (var host in activeHosts)
        {
            // Check ttlSeconds against current time .. keep in mind, client may have disconnected already
            if (request.TTLSeconds < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.Gone, 
                                        "Request has expired: " + DateTimeOffset.UtcNow.ToLocalTime());

                throw new ProxyErrorException(ProxyErrorException.ErrorType.TTLExpired, 
                                            HttpStatusCode.PreconditionFailed, 
                                            "Request has expired:  " + request.Headers["S7PTTL"]);

                // return await WriteProxyDataAsync(new()
                // {
                //     StatusCode = HttpStatusCode.PreconditionFailed,
                //     ResponseMessage = new(HttpStatusCode.InternalServerError)
                //     {
                //         Content = new StringContent("Request has expired:  " + request.Headers["S7PTTL"], Encoding.UTF8)
                //     }
                // });
            }
                    // Try the request on each active host, stop if it worked
            try
            {
                request.Headers.Set("Host", host.Host);
                var urlWithPath = new UriBuilder(host.Url) { Path = request.Path }.Uri.AbsoluteUri;
                request.FullURL = WebUtility.UrlDecode(urlWithPath);

                using (ByteArrayContent bodyContent = new(bodyBytes))
                using (HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                    CopyHeaders(request.Headers, proxyRequest, true);

                    if (bodyBytes.Length > 0)
                    {
                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                        // Preserve the content type if it was provided
                        var contentType = request.Context?.Request.ContentType; // Default to application/octet-stream if not specified
                        var mediaTypeHeaderValue = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");

                        // Preserve the encoding type if it was provided
                        if (request.Context?.Request.ContentType != null && request.Context.Request.ContentType.Contains("charset"))
                        {
                            var charset = request.Context.Request.ContentType.Split(';').LastOrDefault(s => s.Trim().StartsWith("charset"));
                            if (charset != null)
                            {
                                mediaTypeHeaderValue.CharSet = charset.Split('=').Last().Trim();
                            }
                        }
                        else
                        {
                            mediaTypeHeaderValue.CharSet = "utf-8";
                        }

                        proxyRequest.Content.Headers.ContentType = mediaTypeHeaderValue;
                    }

                    proxyRequest.Headers.ConnectionClose = true;

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        _logger.LogDebug($"> {request.Method} {request.FullURL} {bodyBytes.Length} bytes");
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                        //string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    var proxyStartDate = DateTime.UtcNow;
                    Interlocked.Increment(ref states[3]);
                    try {

                        // SEND THE REQUEST TO THE BACKEND 

                        using var proxyResponse = await _options.Client!.SendAsync(
                            proxyRequest,
                            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                        {

                            var responseDate = DateTime.UtcNow;
                            lastStatusCode = proxyResponse.StatusCode;

                            // Check if the status code of the response is in the set of allowed status codes, else try the next host
                            if (((int)proxyResponse.StatusCode > 300 && (int)proxyResponse.StatusCode < 400) || (int)proxyResponse.StatusCode >= 500)
                            {
                                if (request.Debug)
                                {
                                    try
                                    {
                                        ProxyData temp_pr = new()
                                        {
                                            ResponseDate = responseDate,
                                            StatusCode = proxyResponse.StatusCode,
                                            FullURL = request.FullURL,
                                        };
                                        //bodyBytes = [];
                                        Console.WriteLine($"Got: {temp_pr.StatusCode} {temp_pr.FullURL} {temp_pr.ContentHeaders["Content-Length"]} Body: {temp_pr?.Body?.Length} bytes");
                                        Console.WriteLine($"< {temp_pr?.Body}");
                                    }
                                    catch (Exception e)
                                    {
                                        _logger.LogError($"Error reading from backend host: {e.Message}");
                                    }

                                    _logger.LogInformation($"Trying next host: Response: {proxyResponse.StatusCode}");
                                }

                                // The request did not succeed, try the next host

                                continue;
                            }

                            host.AddPxLatency((responseDate - proxyStartDate).TotalMilliseconds);

                            ProxyData pr = new()
                            {
                                ResponseDate = responseDate,
                                StatusCode = proxyResponse.StatusCode,
                                FullURL = request.FullURL,
                                CalculatedHostLatency = host.CalculatedAverageLatency,
                                BackendHostname = host.Host
                            };
                            // the request succeeded, we can clear bodyBytes
                            bodyBytes = [];

                            Interlocked.Increment(ref states[4]);

                            // Read the response
                            //await GetProxyResponseAsync(proxyResponse, request, pr).ConfigureAwait(false);
                            Interlocked.Decrement(ref states[4]);

                            if ((int)proxyResponse.StatusCode == 429 && proxyResponse.Headers.TryGetValues("S7PREQUEUE", out var values))
                            {
                                // Requeue the request if the response is a 429 and the S7PREQUEUE header is set
                                var s7PrequeueValue = values.FirstOrDefault();
                                if (s7PrequeueValue != null && string.Equals(s7PrequeueValue, "true", StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new S7PRequeueException("Requeue request", pr);
                                }
                            }
                            else
                            {
                                // request was successful, so we can disable the skip
                                request.SkipDispose = false;
                            }

                            pr.Headers.Set("S7P-BackendHost", pr.BackendHostname);
                            pr.Headers.Set("S7P-ID", request.MID ?? "N/A");
                            pr.Headers.Set("S7P-Priority", request.Priority.ToString() ?? "N/A");
                            pr.Headers.Set("S7P-Request-Queue-Duration", request.Headers["x-Request-Queue-Duration"] ?? "N/A");
                            pr.Headers.Set("S7P-Request-Process-Duration", request.Headers["x-Request-Process-Duration"] ?? "N/A");


                            // TODO: Move to caller to handle writing errors?
                            // Store the response stream in proxyData and return to parent caller
                            request.Context!.Response.StatusCode = (int)proxyResponse.StatusCode;
                            request.Context.Response.Headers = pr.Headers;

                            // Stream response from the backend to the client

                            await proxyResponse.Content.CopyToAsync(request.Context!.Response.OutputStream).ConfigureAwait(false);
                            //awaitrequest.Context.Response.flush();


//Console.WriteLine($"Writing: to {context} {pr.StatusCode} {pr.FullURL} {pr.ContentHeaders["Content-Length"]} bytes");
                            //await _proxyStreamWriter.WriteDataToStreamAsync(listener, pr, token);

                            //await WriteDataToStreamAsync(request.Context!, pr, token).ConfigureAwait(false);
                            // Log the response if debugging is enabled
                            if (request.Debug)
                            {
                                Console.WriteLine($"Got: {pr.StatusCode} {pr.FullURL}");
                            }
                            return pr ?? throw new ArgumentNullException(nameof(pr));
                        }
                    } finally {
                        Interlocked.Decrement(ref states[3]);

                    }
                }
            }
            catch (S7PRequeueException)
            {
                // rethrow the exception
                throw;
            }
            catch (ProxyErrorException)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                // 408 Request Timeout
                lastStatusCode = HandleProxyRequestError(host, null, request.Timestamp, request.FullURL, HttpStatusCode.RequestTimeout, "Request to " + host.Url + " timed out");
                // log the stack trace 
                //Console.WriteLine($"Error: {e.StackTrace}");
                continue;
            }
            catch (OperationCanceledException e)
            {
                // 502 Bad Gateway
                lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadGateway, "Request to " + host.Url + " was cancelled");
                continue;
            }
            catch (HttpRequestException e)
            {
                // 400 Bad Request
                lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadRequest);
                continue;
            }
            catch (Exception e)
            {
                // 500 Internal Server Error
                _logger.LogError($"Error: {e.StackTrace}");
                _logger.LogError($"Error: {e.Message}");
                lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.InternalServerError);
            }

        }

        // If we get here, then no hosts were able to handle the request
        //ProxyData pd; 
        if (activeHosts.Count() == 1)
        {
            throw new ProxyErrorException(ProxyErrorException.ErrorType.NotProcessed, 
                                          lastStatusCode, 
                                          "Error processing request.");
            // pd = new ProxyData
            // {
            //     StatusCode = lastStatusCode,
            //     ResponseMessage = new HttpResponseMessage(lastStatusCode)
            //      {
            //          Content = new StringContent("Error processing request.", Encoding.UTF8)
            //      }
            // };
        }
        else
        {
            throw new ProxyErrorException(ProxyErrorException.ErrorType.NotProcessed, 
                                          lastStatusCode, 
                                          "No active hosts were able to handle the request.");
            // pd = new ProxyData
            // {
            //     // 502 Bad Gateway 
            //     StatusCode = HttpStatusCode.BadGateway,
            //     ResponseMessage = new(HttpStatusCode.BadGateway)
            //      {
            //          Content = new StringContent("No active hosts were able to handle the request.", Encoding.UTF8)
            //      }
            // };
        }

        //return await WriteProxyDataAsync(pd);
    }



    // Returns false if the TTL is invalid else returns true
    private bool CalculateTTL(RequestData request)
    {
        if (request is not null && request.TTLSeconds == 0)
        {
            if (request.Headers["S7PTTL"] != null)
            {
                string ttlString = request.Headers["S7PTTL"] ?? "";

                // TTL can be specified as +300 ( 300 seconds from now ) or as an absolute number of seconds
                if (ttlString.StartsWith('+') && long.TryParse(ttlString.Substring(1), out long longSeconds))
                {
                    request.TTLSeconds = ((DateTimeOffset)request.EnqueueTime).ToUnixTimeSeconds() + longSeconds;
                }
                else if (long.TryParse(ttlString, out longSeconds))
                {
                    request.TTLSeconds = longSeconds;
                }
                else if (!DateTimeOffset.TryParse(ttlString, out var ttlDate))
                {
                    HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.BadRequest, "Invalid TTL format: " + request.Headers["S7PTTL"]);
                    return false;
                }
                else
                {
                    request.TTLSeconds = ttlDate.ToUnixTimeSeconds();
                }
            }
            else
            {
                // Define a TTL for the request if the S7PTTL header is empty  
                request.TTLSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _options.DefaultTTLSecs;
            }
        }
        return true;
    }

    private static void CopyHeaders(
        NameValueCollection sourceHeaders,
        HttpRequestMessage? targetMessage,
        bool ignoreHeaders = false)
    {
        foreach (var key in sourceHeaders.AllKeys)
        {
            if (key == null) continue;
            if (!ignoreHeaders || (!key.StartsWith("S7P") && !key.StartsWith("X-MS-CLIENT", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
            {
                targetMessage?.Headers.TryAddWithoutValidation(key, sourceHeaders[key]);
            }
        }
    }

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            Console.WriteLine($"{prefix} {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private HttpStatusCode HandleProxyRequestError(
        BackendHostHealth? host, 
        Exception? e, 
        DateTime requestDate, 
        string url, 
        HttpStatusCode statusCode,
        string? customMessage = null)
    {
        // Common operations for all exceptions
        if (_telemetryClient != null)
        {
            if (e != null)
                _telemetryClient.TrackException(e);

            _telemetryClient.TrackEvent(new("ProxyRequest")
            {
                Properties =
                {
                    { "URL", url },
                    { "RequestDate", requestDate.ToString("o") },
                    { "ResponseDate", DateTime.Now.ToString("o") },
                    { "StatusCode", statusCode.ToString() }
                }
            });
        }

        if (!string.IsNullOrEmpty(customMessage))
        {
            _logger.LogError($"{e?.Message ?? customMessage}");
        }
        var date = requestDate.ToString("o");
        _eventClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{url}\", \"Error\":\"{e?.Message ?? customMessage}\"}}");

        host?.AddError();
        return statusCode;
    }

    // Returns probe responses
    //
    // /startup - 200  if all workers started, there is at least 1 active host ... runs at fastest priority
    // /readiness - same as /startup
    // /liveness - 503 if no active hosts  or  there are many recent errors ... runs on high priority
    // /health - details on all active hosts ... runs on high priority

    private void ProbeResponse(string path, out int probeStatus, out string probeMessage)
    {
        probeStatus = 200;
        probeMessage = "OK\n";

        // Cache these to avoid repeatedly calling the same methods
        int activeHosts = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.CheckFailedStatus();

        switch (path)
        {
            case Constants.Health:
                if (activeHosts == 0 || hasFailedHosts)
                {
                    probeStatus = 503;
                    probeMessage = $"Not Healthy.  Active Hosts: {activeHosts} Failed Hosts: {hasFailedHosts}\n";
                }
                else
                {
                    var hosts = _backends.GetHosts();
                    probeMessage = $"Backend Hosts: {"".PadRight(30)} SimpleL7Proxy: {Constants.VERSION}\n Active Hosts: {activeHosts}  -  {(hasFailedHosts ? "FAILED HOSTS" : "All Hosts Operational")}\n";
                    if (hosts.Count > 0)
                    {
                        foreach (var host in hosts)
                        {
                            probeMessage += $" Name: {host.Host}  Status: {host.GetStatus(out int calls, out int errorCalls, out double average)}\n";
                        }
                    }
                    else
                    {
                        probeMessage += "No Hosts\n";
                    }

                    probeMessage += $"Worker Statistics:\n {GetState()}\n";
                    probeMessage += $"User Priority Queue: {_userPriority?.GetState() ?? "N/A"}\n";
                }
                break;

            case Constants.Readiness:
            case Constants.Startup:
                if (!readyToWork || activeHosts == 0)
                {
                    probeStatus = 503;
                    probeMessage = "Not Ready .. activeHosts = " + activeHosts + " readyToWork = " + readyToWork;
                }
                break;

            case Constants.Liveness:
                if (activeHosts == 0)
                {
                    probeStatus = 503;
                    probeMessage = $"Not Lively.  Active Hosts: {activeHosts} Failed Hosts: {hasFailedHosts}";
                }
                break;

            case Constants.Shutdown:
                // Shutdown is a signal to unwedge workers and shut down gracefully
                break;
        }
    }

    // // Read the response from the proxy and set the response body
    // private async Task GetProxyResponseAsync(HttpResponseMessage proxyResponse, RequestData request, ProxyData pr)
    // {
    //     // Get a stream to the response body
    //     await using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync().ConfigureAwait(false))
    //     {
    //         if (request.Debug)
    //         {
    //             LogHeaders(proxyResponse.Headers, "<");
    //             LogHeaders(proxyResponse.Content.Headers, "  <");
    //         }

    //         // Copy across all the response headers to the client
    //         CopyHeaders(proxyResponse, pr.Headers, pr.ContentHeaders);
    //         pr.Headers.Add("S7P-BackendHost", pr.BackendHostname);
    //         pr.Headers.Add("S7P-ID", request.MID ?? "N/A");
    //         pr.Headers.Add("S7P-Priority", request.Priority.ToString() ?? "N/A");
    //         pr.Headers.Add("S7P-Request-Queue-Duration", request.Headers["x-Request-Queue-Duration"] ?? "N/A");
    //         pr.Headers.Add("S7P-Request-Process-Duration", request.Headers["x-Request-Process-Duration"] ?? "N/A");

    //         // Determine the encoding from the Content-Type header
    //         MediaTypeHeaderValue? contentType = proxyResponse.Content.Headers.ContentType;
    //         var encoding = GetEncodingFromContentType(contentType, request);

    //         using (var reader = new StreamReader(responseBody, encoding))
    //         {
    //             pr.Body = encoding.GetBytes(await reader.ReadToEndAsync().ConfigureAwait(false));
    //         }
    //     }
    // }

    // private Encoding GetEncodingFromContentType(MediaTypeHeaderValue? contentType, RequestData request)
    // {
    //     if (string.IsNullOrWhiteSpace(contentType?.CharSet))
    //     {
    //         if (request.Debug)
    //         {
    //             Console.WriteLine("< No charset specified, using default UTF-8");
    //         }
    //         return Encoding.UTF8;
    //     }

    //     try
    //     {
    //         return Encoding.GetEncoding(contentType.CharSet);
    //     }
    //     catch (ArgumentException)
    //     {
    //         HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.UnsupportedMediaType,
    //             $"Unsupported charset: {contentType.CharSet}");
    //         return Encoding.UTF8; // Fallback to UTF-8 in case of error
    //     }
    // }
    
    // private void CopyHeadersToResponse(WebHeaderCollection sourceHeaders, WebHeaderCollection targetHeaders)
    // {
    //     foreach (var key in sourceHeaders.AllKeys)
    //     {
    //         //Console.WriteLine($"Copying {key} : {sourceHeaders[key]}");
    //         targetHeaders.Add(key, sourceHeaders[key]);
    //     }
    // }

    // private void CopyHeaders(HttpResponseMessage sourceMessage, WebHeaderCollection targetHeaders, WebHeaderCollection? targetContentHeaders = null)
    // {
    //     foreach (var header in sourceMessage.Headers)
    //     {
    //         targetHeaders.Add(header.Key, string.Join(", ", header.Value));
    //     }
    //     if (targetContentHeaders != null)
    //     {
    //         foreach (var header in sourceMessage.Content.Headers)
    //         {
    //             targetContentHeaders.Add(header.Key, string.Join(", ", header.Value));
    //         }
    //     }
    // }

}