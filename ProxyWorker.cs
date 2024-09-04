
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;


// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker  {

    private static bool _debug = false;
    private  CancellationToken _cancellationToken;
    //private  BlockingCollection<RequestData>? _requestsQueue; 
    private  BlockingPriorityQueue<RequestData>? _requestsQueue; 
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient;
    private readonly IEventHubClient? _eventHubClient;


    //public ProxyWorker( CancellationToken cancellationToken, BlockingCollection<RequestData> requestsQueue, BackendOptions backendOptions, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient) {
    public ProxyWorker(CancellationToken cancellationToken, BlockingPriorityQueue<RequestData> requestsQueue, BackendOptions backendOptions, IBackendService? backends, IEventHubClient? eventHubClient, TelemetryClient? telemetryClient) 
    {
        _cancellationToken = cancellationToken;
        _requestsQueue = requestsQueue ?? throw new ArgumentNullException(nameof(requestsQueue));
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _eventHubClient = eventHubClient;
        _telemetryClient = telemetryClient;
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));
    }

    public async Task TaskRunner() {
        if (_requestsQueue == null) throw new ArgumentNullException(nameof(_requestsQueue));

        while (!_cancellationToken.IsCancellationRequested)
        {
            RequestData incomingRequest;
            try
            {
                //incomingRequest = _requestsQueue.Take(_cancellationToken); // This will block until an item is available or the token is cancelled
                incomingRequest = _requestsQueue.Dequeue(_cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break; // Exit the loop if the operation is cancelled
            }

            await using (incomingRequest ) {

                var lcontext = incomingRequest?.Context;

                if (lcontext == null || incomingRequest == null) {
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

                    var pr = await ReadProxyAsync(incomingRequest).ConfigureAwait(false);
                    await WriteResponseAsync(lcontext, pr).ConfigureAwait(false);

                    Console.WriteLine($"Pri: {incomingRequest.Priority} Stat: {(int)pr.StatusCode} Len: {pr.ContentHeaders["Content-Length"]} {pr.FullURL}");

                    if (_eventHubClient != null)
                        SendEventData(pr.FullURL, pr.StatusCode, incomingRequest.Timestamp, pr.ResponseDate);

                    pr.Body=[];
                    pr.ContentHeaders.Clear();
                    pr.FullURL="";
                }
                catch (IOException ioEx) {
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
                }
                catch (Exception ex)
                {
                    if (ex.Message == "Cannot access a disposed object.") // The client likely closed the connection
                    {
                        Console.WriteLine($"Client closed connection: {incomingRequest.FullURL}");
                        continue;
                    }
                    // Log the exception
                    Console.WriteLine($"Exception: {ex.Message}");
                    Console.WriteLine($"Stack Trace: {ex.StackTrace}");

                    // Set an appropriate status code for the error
                    lcontext.Response.StatusCode = 500;
                    var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
                    try
                    { 
                        Dictionary<string, string> prop = new Dictionary<string, string>();
                        prop["Method"] = incomingRequest.Method;
                        prop["Path"] = incomingRequest.Path;
                        prop["StatusCode"] = lcontext.Response.StatusCode.ToString();
                        prop["Message"] = ex.Message;
                        _telemetryClient?.TrackException(ex, prop);
                        await lcontext.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                    }
                    catch (Exception writeEx)
                    {
                        Console.WriteLine($"Failed to write error message: {writeEx.Message}");
                    } 
                }
                finally
                {
                    _telemetryClient?.TrackRequest($"{incomingRequest.Method} {incomingRequest.Path}", 
                        DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{lcontext.Response.StatusCode}", true);
                }
            }
        }
    }

    private async Task WriteResponseAsync(HttpListenerContext context, ProxyData pr) 
    {
        // Set the response status code
        context.Response.StatusCode = (int)pr.StatusCode;

        // Copy headers to the response
        CopyHeaders(pr.Headers, context.Response.Headers);

        // Set content-specific headers
        if (pr.ContentHeaders != null)
        {
            foreach (var key in pr.ContentHeaders.AllKeys)
            {
                switch (key.ToLowerInvariant())
                {
                    case "content-length":
                        var length = pr.ContentHeaders[key];
                        if (long.TryParse(length, out var contentLength))
                        {
                            context.Response.ContentLength64 = contentLength;
                        }
                        else
                        {
                            Console.WriteLine($"Invalid Content-Length: {length}");
                        }
                        break;

                    case "content-type":
                        context.Response.ContentType = pr.ContentHeaders[key];
                        break;

                    default:
                        context.Response.Headers[key] = pr.ContentHeaders[key];
                        break;
                }
            }
        }
        context.Response.KeepAlive = false;

        // Write the response body to the client as a byte array
        if (pr.Body != null)
        {
            await using (var memoryStream = new MemoryStream(pr.Body))
            {
                await memoryStream.CopyToAsync(context.Response.OutputStream);
                await context.Response.OutputStream.FlushAsync();
            }
        }
    }

public async Task<ProxyData> ReadProxyAsync(RequestData request) //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    {
        if (request == null) throw new ArgumentNullException(nameof(request), "Request cannot be null.");
        if (request.Body == null) throw new ArgumentNullException(nameof(request.Body), "Request body cannot be null.");
        if (request.Headers == null) throw new ArgumentNullException(nameof(request.Headers), "Request headers cannot be null.");
        if (request.Method == null) throw new ArgumentNullException(nameof(request.Method), "Request method cannot be null.");

        // Use the current active hosts
        var activeHosts = _backends.GetActiveHosts();

        request.Debug = request.Headers["S7PDEBUG"] == "true" || _debug;
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;

        // Read the body stream once and reuse it
        byte[] bodyBytes;
        using (MemoryStream ms = new MemoryStream())
        {
            await request.Body.CopyToAsync(ms);
            bodyBytes = ms.ToArray();
        }

        if (_options.UseOAuth) {
            // Get a token
            var token = _backends.OAuth2Token();
            if (request.Debug)  {
                Console.WriteLine("Token: " + token);
            }
            // Set the token in the headers
            request.Headers.Set("Authorization", $"Bearer {token}");
        }
        
        foreach (var host in activeHosts)
        {
            // Try the request on each active host, stop if it worked
            try
            {
                request.Headers.Set("Host", host.host);
                var urlWithPath = new UriBuilder(host.url) { Path = request.Path }.Uri.AbsoluteUri;
                request.FullURL = System.Net.WebUtility.UrlDecode(urlWithPath);

                using (var bodyContent = new ByteArrayContent(bodyBytes))
                using (var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;
                    
                    CopyHeaders( request.Headers, proxyRequest, true);
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

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        Console.WriteLine($"> {request.Method} {request.FullURL} {bodyBytes.Length} bytes");
                        LogHeaders(proxyRequest.Headers, ">");
                        LogHeaders(proxyRequest.Content.Headers, "  >");
                        string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    // Send the request and get the response
                    var ProxyStartDate = DateTime.UtcNow;
                    using (var proxyResponse = await (_options?.Client ?? throw new ArgumentNullException(nameof(_options))).SendAsync(proxyRequest))
                    {
                        var responseDate = DateTime.UtcNow;
                        lastStatusCode = proxyResponse.StatusCode;

                        // Check if the status code of the response is in the set of allowed status codes, else try the next host
                       if (((int)proxyResponse.StatusCode > 300 &&  (int)proxyResponse.StatusCode < 400) || (int)proxyResponse.StatusCode > 500)
                       {

                           if (request.Debug) {
                               try {
                                    var temp_pr = new ProxyData()
                                    {
                                        ResponseDate = responseDate,
                                        StatusCode = proxyResponse.StatusCode,
                                        FullURL = request.FullURL,
                                    };
                                    bodyBytes = [];
                                    await GetProxyResponseAsync(proxyResponse, request, temp_pr);
                                    Console.WriteLine($"Got: {temp_pr.StatusCode} {temp_pr.FullURL} {temp_pr.ContentHeaders["Content-Length"]} Body: {temp_pr?.Body?.Length} bytes"); 
                                    Console.WriteLine($"< {temp_pr?.Body }"); 
                               } catch (Exception e) {
                                    Console.WriteLine($"Error reading from remote host: {e.Message}");
                               }

                               Console.WriteLine($"Trying next host: Response: {proxyResponse.StatusCode}");
                           }
                           continue;
                       }

                        host.AddPxLatency((responseDate - ProxyStartDate).TotalMilliseconds);

                        // Read the response
                        var pr = new ProxyData()
                        {
                            ResponseDate = responseDate,
                            StatusCode = proxyResponse.StatusCode,
                            FullURL = request.FullURL,
                        };
                        bodyBytes = [];
                        await GetProxyResponseAsync(proxyResponse, request, pr);

                        if (request.Debug)
                        {
                            Console.WriteLine($"Got: {pr.StatusCode} {pr.FullURL} {pr.ContentHeaders["Content-Length"]} Body: {pr?.Body?.Length} bytes");  
                        }
                        return pr ?? throw new ArgumentNullException(nameof(pr));
                    }
                }
            }

            catch (TaskCanceledException)
            {
                // 408 Request Timeout
                lastStatusCode = HandleError(host, null, request.Timestamp, request.FullURL, HttpStatusCode.RequestTimeout, "Request to " + host.url + " timed out"); 
                continue;
            }
            catch (OperationCanceledException e)
            {
                // 502 Bad Gateway
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadGateway, "Request to " + host.url + " was cancelled");
                continue;
            }
            catch (HttpRequestException e)
            {
                // 400 Bad Request
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.BadRequest);
                continue;
            }
            catch (Exception e)
            {
                // 500 Internal Server Error
                Console.WriteLine($"Error: {e.StackTrace}");
                Console.WriteLine($"Error: {e.Message}");
                lastStatusCode = HandleError(host, e, request.Timestamp, request.FullURL, HttpStatusCode.InternalServerError);
            }
        }

        // If we get here, then no hosts were able to handle the request
        //Console.WriteLine($"{path}  - {lastStatusCode}");

        return new ProxyData
        {
            // 502 Bad Gateway
            StatusCode = HttpStatusCode.BadGateway,
            Body = Encoding.UTF8.GetBytes("No active hosts were able to handle the request.")
        };


    }

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
                pr.Body= encoding.GetBytes(await reader.ReadToEndAsync());
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
            HandleError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.UnsupportedMediaType,
                $"Unsupported charset: {contentType.CharSet}");
            return Encoding.UTF8; // Fallback to UTF-8 in case of error
        }
    }

    private void CopyHeaders(NameValueCollection sourceHeaders, HttpRequestMessage? targetMessage, bool ignoreHeaders = false)
    {
        foreach (string? key in sourceHeaders.AllKeys)
        {
            if (key == null) continue;
            if ( !ignoreHeaders ||  (!key.StartsWith("S7P") && !key.StartsWith("x-", StringComparison.OrdinalIgnoreCase) &&  !key.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
            {
                targetMessage?.Headers.TryAddWithoutValidation(key, sourceHeaders[key]);
            }
        }
    }

    private void CopyHeaders(WebHeaderCollection sourceHeaders, WebHeaderCollection targetHeaders)
    {
        foreach (var key in sourceHeaders.AllKeys)
        {
            targetHeaders.Add(key, sourceHeaders[key]);
        }
    }

    private void CopyHeaders(HttpResponseMessage sourceMessage, WebHeaderCollection targetHeaders, WebHeaderCollection? targetContentHeaders=null)
    {
        foreach (var header in sourceMessage.Headers)
        {
            targetHeaders.Add(header.Key, string.Join(", ", header.Value));
        }
        if (targetContentHeaders != null) {
            foreach (var header in sourceMessage.Content.Headers)
            {
                targetContentHeaders.Add(header.Key, string.Join(", ", header.Value));
            }
        }
    }

    private void SendEventData(string urlWithPath, HttpStatusCode statusCode, DateTime requestDate, DateTime responseDate)
    {
        string date = responseDate.ToString("o");
        var delta = (responseDate - requestDate).ToString(@"ss\:fff");
        _eventHubClient?.SendData($"{{\"Date\":\"{date}\", \"Url\":\"{urlWithPath}\", \"Status\":\"{statusCode}\", \"Latency\":\"{delta}\"}}");
    }

    private void LogHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers, string prefix)
    {
        foreach (var header in headers)
        {
            Console.WriteLine($"{prefix} {header.Key} : {string.Join(", ", header.Value)}");
        }
    }

    private HttpStatusCode HandleError(BackendHost? host, Exception? e, DateTime requestDate, string url, HttpStatusCode statusCode, string? customMessage = null)
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