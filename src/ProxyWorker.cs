using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using static Microsoft.AspNetCore.Hosting.Internal.HostingApplication;


// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker
{

    private static bool _debug = false;
    private CancellationToken _cancellationToken;
    //private  BlockingCollection<RequestData>? _requestsQueue; 
    private BlockingPriorityQueue<RequestData>? _requestsQueue;
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient;
    private readonly IEventHubClient? _eventHubClient;
    private string IDstr = "";


    //public ProxyWorker( CancellationToken cancellationToken, BlockingCollection<RequestData> requestsQueue, BackendOptions backendOptions, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient) {
    public ProxyWorker(CancellationToken cancellationToken, int ID, BlockingPriorityQueue<RequestData> requestsQueue, BackendOptions backendOptions, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient)
    {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
        IDstr = ID.ToString();
    }

    public async Task TaskRunner()
    {
        if (_requestsQueue == null) throw new ArgumentNullException(nameof(_requestsQueue));

        while (!_cancellationToken.IsCancellationRequested)
        {
            RequestData incomingRequest;
            try
            {
                //incomingRequest = _requestsQueue.Take(_cancellationToken); // This will block until an item is available or the token is cancelled

                incomingRequest = _requestsQueue.Dequeue(_cancellationToken, IDstr); // This will block until an item is available or the token is cancelled
                incomingRequest.DequeueTime = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                //Console.WriteLine("Operation was cancelled. Stopping the worker.");
                break; // Exit the loop if the operation is cancelled
            }

            await using (incomingRequest)
            {
                var requestWasRetried = false;
                var lcontext = incomingRequest?.Context;
                bool isExpired = false;

                Dictionary<string, string> eventData = new Dictionary<string, string>();
                eventData["ProxyHost"] = _options.HostName;
                eventData["Date"] = incomingRequest?.DequeueTime.ToString("o") ?? DateTime.UtcNow.ToString("o");
                eventData["Path"] = incomingRequest?.Path ?? "N/A";
                eventData["x-RequestPriority"] = incomingRequest?.Priority.ToString() ?? "N/A";
                eventData["x-RequestMethod"] = incomingRequest?.Method ?? "N/A";
                eventData["x-RequestPath"] = incomingRequest?.Path ?? "N/A";
                eventData["x-RequestHost"] = incomingRequest?.Headers["Host"] ?? "N/A";
                eventData["x-RequestUserAgent"] = incomingRequest?.Headers["User-Agent"] ?? "N/A";
                eventData["x-RequestContentType"] = incomingRequest?.Headers["Content-Type"] ?? "N/A";
                eventData["x-RequestContentLength"] = incomingRequest?.Headers["Content-Length"] ?? "N/A";
                eventData["x-RequestWorker"] = IDstr;


                if (lcontext == null || incomingRequest == null)
                {
                    continue;
                }

                try
                {

                    if (incomingRequest.Path == "/health")
                    {
                        lcontext.Response.StatusCode = 200;
                        lcontext.Response.ContentType = "text/plain";
                        lcontext.Response.Headers.Add("Cache-Control", "no-cache");
                        lcontext.Response.KeepAlive = false;

                        Byte[]? healthMessage = Encoding.UTF8.GetBytes(_backends?.HostStatus() ?? "OK");
                        lcontext.Response.ContentLength64 = healthMessage.Length;

                        await lcontext.Response.OutputStream.WriteAsync(healthMessage, 0, healthMessage.Length);
                        continue;
                    }

                    incomingRequest.Headers["x-Request-Queue-Duration"] = (incomingRequest.DequeueTime - incomingRequest.EnqueueTime).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Process-Duration"] = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString();
                    incomingRequest.Headers["x-Request-Worker"] = IDstr;
                    incomingRequest.Headers["x-S7PID"] = incomingRequest.MID ?? "N/A";
                    incomingRequest.Headers["x-S7PPriority"] = incomingRequest.Priority.ToString() ?? "N/A";

                    eventData["x-Request-Queue-Duration"] = incomingRequest.Headers["x-Request-Queue-Duration"] ?? "N/A";
                    eventData["x-Request-Process-Duration"] = incomingRequest.Headers["x-Request-Process-Duration"] ?? "N/A";
                    eventData["x-S7PID"] = incomingRequest.MID ?? "N/A";

                    var pr = await ReadProxyAsync(incomingRequest).ConfigureAwait(false);
                    eventData["x-Status"] = ((int)pr.StatusCode).ToString();
                    if (_options.LogHeaders != null && _options.LogHeaders.Count > 0)
                    {
                        foreach (var header in _options.LogHeaders)
                        {
                            eventData[header] = lcontext.Response?.Headers[header] ?? "N/A";
                        }
                    }

                    if (pr.StatusCode == HttpStatusCode.Gone) // 410 Gone
                    {
                        // Request has expired
                        isExpired = true;
                    }

                    Console.WriteLine($"Pri: {incomingRequest.Priority} Stat: {(int)pr.StatusCode} Len: {pr.ContentHeaders["Content-Length"]} {pr.FullURL}");

                    eventData["Url"] = pr.FullURL;
                    eventData["x-Response-Latency"] = (pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    eventData["x-Total-Latency"] = (DateTime.Now - incomingRequest.Timestamp).TotalMilliseconds.ToString("F3");
                    eventData["x-Backend-Host"] = pr?.BackendHostname ?? "N/A";
                    eventData["x-Backend-Host-Latency"] = pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
                    eventData["Content-Length"] = lcontext.Response?.ContentLength64.ToString() ?? "N/A";
                    eventData["Content-Type"] = lcontext?.Response?.ContentType ?? "N/A";

                    if (_eventHubClient != null)
                    {
                        eventData["Type"] = "ProxyRequest";
                        if (isExpired)
                        {
                            eventData["Type"] = "Expired-ProxyRequest";
                        }

                        SendEventData(eventData);
                    }
                }
                catch (S7PRequeueException e)
                {
                    // Requeue the request 
                    _requestsQueue.Requeue(incomingRequest, incomingRequest.Priority, incomingRequest.EnqueueTime);
                    Console.WriteLine($"Requeued request.  Pri: {incomingRequest.Priority} Queue Length: {_requestsQueue.Count} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}");
                    requestWasRetried = true;
                    incomingRequest.SkipDispose = true;
                    eventData["Url"] = e.pr.FullURL;
                    eventData["x-Status"] = ((int)503).ToString();
                    eventData["x-Response-Latency"] = (e.pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
                    eventData["x-Total-Latency"] = (DateTime.Now - incomingRequest.Timestamp).TotalMilliseconds.ToString("F3");
                    eventData["x-Backend-Host"] = e.pr?.BackendHostname ?? "N/A";
                    eventData["x-Backend-Host-Latency"] = e.pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
                    eventData["Type"] = "Requeue-ProxyRequest";

                    SendEventData(eventData);

                }
                catch (IOException ioEx)
                {
                    if (isExpired)
                    {
                        Console.WriteLine("IoException on an exipred request");
                        continue;
                    }

                    Console.WriteLine($"An IO exception occurred: {ioEx.Message}");
                    lcontext.Response.StatusCode = 502;
                    var errorMessage = Encoding.UTF8.GetBytes($"Broken Pipe: {ioEx.Message}");
                    try
                    {
                        await lcontext.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                    }
                    catch (Exception writeEx)
                    {
                        Console.WriteLine($"Failed to write error message: {writeEx.Message}");
                    }

                    eventData["x-Status"] = "502";
                    eventData["Type"] = "IOException";
                    eventData["x-Message"] = ioEx.Message;

                    SendEventData(eventData);

                }
                catch (Exception ex)
                {
                    if (isExpired)
                    {
                        Console.WriteLine("IoException on an exipred request");
                        continue;
                    }
                    if (ex.Message == "Cannot access a disposed object.") // The client likely closed the connection
                    {
                        Console.WriteLine($"Client closed connection: {incomingRequest.FullURL}");
                        continue;
                    }
                    // Log the exception
                    Console.WriteLine($"Exception: {ex.Message}");
                    Console.WriteLine($"Stack Trace: {ex.StackTrace}");

                    eventData["x-Status"] = "500";
                    eventData["Type"] = "IOException";
                    eventData["x-Message"] = ex.Message;

                    // Set an appropriate status code for the error
                    lcontext.Response.StatusCode = 500;
                    var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
                    try
                    {
                        _telemetryClient?.TrackException(ex, eventData);
                        await lcontext.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                    }
                    catch (Exception writeEx)
                    {
                        Console.WriteLine($"Failed to write error message: {writeEx.Message}");
                    }
                }
                finally
                {

                    // Let's not track the request if it was retried.
                    if (!requestWasRetried)
                    {
                        // Track the status of the request for circuit breaker
                        _backends.TrackStatus((int)lcontext.Response.StatusCode);

                        _telemetryClient?.TrackRequest($"{incomingRequest.Method} {incomingRequest.Path}",
                            DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{lcontext.Response.StatusCode}", true);
                        _telemetryClient?.TrackEvent("ProxyRequest", eventData);

                        lcontext?.Response.Close();
                    }

                }

            }
        }

        Console.WriteLine("Worker stopped.");
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

    private async Task WriteResponseAsync(RequestData request, HttpResponseMessage proxyResponse, ProxyData pr)
    {
        if (request.Context == null) throw new ArgumentNullException(nameof(request), "Request context cannot be null.");

        // Set the response status code  
        request.Context.Response.StatusCode = (int)proxyResponse.StatusCode;

        // Copy headers to the response  
        CopyHeadersToResponse(request.Context.Response.Headers, request.Context.Response.Headers);

        if (proxyResponse.Content.Headers != null)
        {
            foreach (var header in proxyResponse.Content.Headers)
            {
                request.Context.Response.Headers[header.Key] = string.Join(", ", header.Value);

                if(header.Key.ToLower().Equals("content-length"))
                {
                    request.Context.Response.ContentLength64 = proxyResponse.Content.Headers.ContentLength ?? 0;
                }
            }
        }

        request.Context.Response.KeepAlive = false;

        // Stream the response body to the client  
        if (proxyResponse.Content != null)
        {
            await using var responseStream = await proxyResponse.Content.ReadAsStreamAsync();
            await responseStream.CopyToAsync(request.Context.Response.OutputStream);
            await request.Context.Response.OutputStream.FlushAsync();
        }
    }

    public async Task<ProxyData> ReadProxyAsync(RequestData request) //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    {
        if (request == null) throw new ArgumentNullException(nameof(request), "Request cannot be null.");
        if (request.Body == null) throw new ArgumentNullException(nameof(request.Body), "Request body cannot be null.");
        if (request.Headers == null) throw new ArgumentNullException(nameof(request.Headers), "Request headers cannot be null.");
        if (request.Method == null) throw new ArgumentNullException(nameof(request.Method), "Request method cannot be null.");

        var activeHosts = _backends.GetActiveHosts();
        request.Debug = _debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));

        byte[] bodyBytes = await request.CachBodyAsync();
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;

        if (request.TTLSeconds == 0)
        {
            if (!CalculateTTL(request))
            {
                return new ProxyData
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    Body = Encoding.UTF8.GetBytes("Invalid TTL format: " + request.Headers["S7PTTL"])
                };
            }
        }

        // Check ttlSeconds against current time .. keep in mind, client may have disconnected already
        if (request.TTLSeconds < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.Gone, "Request has expired: " + DateTimeOffset.UtcNow.ToLocalTime());
            return new ProxyData
            {
                StatusCode = HttpStatusCode.Gone,
                Body = Encoding.UTF8.GetBytes("Request has expired: " + request.Headers["S7PTTL"])
            };
        }

        if (_options.UseOAuth)
        {
            // Get a token
            var token = _backends.OAuth2Token();
            if (request.Debug)
            {
                Console.WriteLine("Token: " + token);
            }
            // Set the token in the headers
            request.Headers.Set("Authorization", $"Bearer {token}");
        }

        foreach (var host in activeHosts)
        {
            try
            {
                request.Headers.Set("Host", host.host);
                var urlWithPath = new UriBuilder(host.url) { Path = request.Path }.Uri.AbsoluteUri;
                request.FullURL = System.Net.WebUtility.UrlDecode(urlWithPath);

                using (var bodyContent = new ByteArrayContent(bodyBytes))
                using (var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                    CopyHeaders(request.Headers, proxyRequest, true);

                    if (bodyBytes.Length > 0)
                    {
                        proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                        // Preserve the content type if it was provided
                        string contentType = request.Context?.Request.ContentType ?? "application/octet-stream"; // Default to application/octet-stream if not specified
                        var mediaTypeHeaderValue = new MediaTypeHeaderValue(contentType);

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

                    if (request.Debug)
                    {
                        Console.WriteLine($"> {request.Method} {request.FullURL} {bodyBytes.Length} bytes");
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                    }

                    var ProxyStartDate = DateTime.UtcNow;
                    using (var proxyResponse = await (_options?.Client ?? throw new ArgumentNullException(nameof(_options)))
                        .SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, _cancellationToken))
                    {
                        var responseDate = DateTime.UtcNow;
                        lastStatusCode = proxyResponse.StatusCode;

                        if (((int)proxyResponse.StatusCode >= 300 && (int)proxyResponse.StatusCode < 400) || (int)proxyResponse.StatusCode >= 500)
                        {
                            if (request.Debug)
                            {
                                Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                            }
                            continue;
                        }

                        host.AddPxLatency((responseDate - ProxyStartDate).TotalMilliseconds);

                        var pr = new ProxyData()
                        {
                            ResponseDate = responseDate,
                            StatusCode = proxyResponse.StatusCode,
                            FullURL = request.FullURL,
                            CalculatedHostLatency = host.calculatedAverageLatency,
                            BackendHostname = host.host
                        };

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

                        await WriteResponseAsync(request, proxyResponse, pr);

                        if (request.Debug)
                        {
                            Console.WriteLine($"Got: {pr.StatusCode} {pr.FullURL}");
                        }
                        return pr ?? throw new ArgumentNullException(nameof(pr));
                    }
                }
            }
            catch (S7PRequeueException)
            {
                // rethrow the exception
                throw;
            }
            catch (TaskCanceledException)
            {
                // 408 Request Timeout
                lastStatusCode = HandleProxyRequestError(host, null, request.Timestamp, request.FullURL, HttpStatusCode.RequestTimeout, "Request to " + host.url + " timed out");
                // log the stack trace 
                //Console.WriteLine($"Error: {e.StackTrace}");
                continue;
            }
            catch (OperationCanceledException e)
            {
                // 502 Bad Gateway
                lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadGateway, "Request to " + host.url + " was cancelled");
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
                Console.WriteLine($"Error: {e.StackTrace}");
                Console.WriteLine($"Error: {e.Message}");
                lastStatusCode = HandleProxyRequestError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.InternalServerError);
            }
        }

        return new ProxyData
        {
            StatusCode = HttpStatusCode.BadGateway,
            Body = Encoding.UTF8.GetBytes("No active hosts were able to handle the request.")
        };
    }


    // Returns false if the TTL is invalid else returns true
    private bool CalculateTTL(RequestData request)
    {
        if (request is not null && request.TTLSeconds == 0)
        {
            if (request.Headers["S7PTTL"] != null)
            {
                long longSeconds;
                string ttlString = request.Headers["S7PTTL"] ?? "";

                // TTL can be specified as +300 ( 300 seconds from now ) or as an absolute number of seconds
                if (ttlString.StartsWith("+") && long.TryParse(ttlString.Substring(1), out longSeconds))
                {
                    request.TTLSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + longSeconds;
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

    // Read the response from the proxy and set the response body
    private async Task GetProxyResponseAsync(HttpResponseMessage proxyResponse, RequestData request, ProxyData pr)
    {
        // Get a stream to the response body
        await using (var responseBody = await proxyResponse.Content.ReadAsStreamAsync())
        {
            if (request.Debug)
            {
                LogHeaders(proxyResponse.Headers, "<");
                LogHeaders(proxyResponse.Content.Headers, "  <");
            }

            // Copy across all the response headers to the client
            CopyHeaders(proxyResponse, pr.Headers, pr.ContentHeaders);

            // Determine the encoding from the Content-Type header
            MediaTypeHeaderValue? contentType = proxyResponse.Content.Headers.ContentType;
            var encoding = GetEncodingFromContentType(contentType, request);

            using (var reader = new StreamReader(responseBody, encoding))
            {
                pr.Body = encoding.GetBytes(await reader.ReadToEndAsync());
            }
        }
    }

    private Encoding GetEncodingFromContentType(MediaTypeHeaderValue? contentType, RequestData request)
    {
        if (string.IsNullOrWhiteSpace(contentType?.CharSet))
        {
            if (request.Debug)
            {
                Console.WriteLine("< No charset specified, using default UTF-8");
            }
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(contentType.CharSet);
        }
        catch (ArgumentException)
        {
            HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.UnsupportedMediaType,
                $"Unsupported charset: {contentType.CharSet}");
            return Encoding.UTF8; // Fallback to UTF-8 in case of error
        }
    }

    private void CopyHeaders(NameValueCollection sourceHeaders, HttpRequestMessage? targetMessage, bool ignoreHeaders = false)
    {
        foreach (string? key in sourceHeaders.AllKeys)
        {
            if (key == null) continue;
            if (!ignoreHeaders || (!key.StartsWith("S7P") && !key.StartsWith("X-MS-CLIENT", StringComparison.OrdinalIgnoreCase) && !key.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
            {
                targetMessage?.Headers.TryAddWithoutValidation(key, sourceHeaders[key]);
            }
        }
    }

    private void CopyHeadersToResponse(WebHeaderCollection sourceHeaders, WebHeaderCollection targetHeaders)
    {
        foreach (var key in sourceHeaders.AllKeys)
        {
            //Console.WriteLine($"Copying {key} : {sourceHeaders[key]}");
            targetHeaders.Add(key, sourceHeaders[key]);
        }
    }

    private void CopyHeaders(HttpResponseMessage sourceMessage, WebHeaderCollection targetHeaders, WebHeaderCollection? targetContentHeaders = null)
    {
        foreach (var header in sourceMessage.Headers)
        {
            targetHeaders.Add(header.Key, string.Join(", ", header.Value));
        }
        if (targetContentHeaders != null)
        {
            foreach (var header in sourceMessage.Content.Headers)
            {
                targetContentHeaders.Add(header.Key, string.Join(", ", header.Value));
            }
        }
    }

    private void SendEventData(Dictionary<string, string> eventData)//string urlWithPath, HttpStatusCode statusCode, DateTime requestDate, DateTime responseDate)
    {
        // string date = responseDate.ToString("o");
        // var delta = (responseDate - requestDate).ToString(@"ss\:fff");
        // _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{urlWithPath}\", \"Status\":\"{statusCode}\", \"Latency\":\"{delta}\"}}");

        string jsonData = JsonSerializer.Serialize(eventData);
        _eventHubClient?.SendData(jsonData);
    }

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            Console.WriteLine($"{prefix} {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private HttpStatusCode HandleProxyRequestError(BackendHost? host, Exception? e, DateTime requestDate, string url, HttpStatusCode statusCode, string? customMessage = null)
    {
        // Common operations for all exceptions

        if (_telemetryClient != null)
        {
            if (e != null)
                _telemetryClient.TrackException(e);

            var telemetry = new EventTelemetry("ProxyRequest");
            telemetry.Properties.Add("URL", url);
            telemetry.Properties.Add("RequestDate", requestDate.ToString("o"));
            telemetry.Properties.Add("ResponseDate", DateTime.Now.ToString("o"));
            telemetry.Properties.Add("StatusCode", statusCode.ToString());
            _telemetryClient.TrackEvent(telemetry);
        }


        if (!string.IsNullOrEmpty(customMessage))
        {
            Console.WriteLine($"{e?.Message ?? customMessage}");
        }
        var date = requestDate.ToString("o");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{url}\", \"Error\":\"{e?.Message ?? customMessage}\"}}");

        host?.AddError();
        return statusCode;
    }

}
