using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;

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
    private static readonly bool _debug = false;
    private readonly CancellationToken _cancellationToken;
    private readonly IBlockingPriorityQueue<RequestData> _requestsQueue;
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly TelemetryClient? _telemetryClient;
    private readonly IEventClient _eventClient;
    private readonly ILogger<ProxyWorker> _logger;
    private readonly ProxyStreamWriter _proxyStreamWriter;
    private readonly string IDstr = "";

    public ProxyWorker(
      int ID,
      IBlockingPriorityQueue<RequestData> requestsQueue, 
      BackendOptions backendOptions, 
      IBackendService? backends, 
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
        _telemetryClient = telemetryClient;
        _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        if (_options.Client == null) throw new NullReferenceException(nameof(_options.Client));
        IDstr = ID.ToString();
    }

  public async Task TaskRunner()
  {
    while (!_cancellationToken.IsCancellationRequested)
    {
      RequestData incomingRequest;
      try
      {
        incomingRequest = await _requestsQueue.Dequeue(IDstr, _cancellationToken); // This will block until an item is available or the token is cancelled
        incomingRequest.DequeueTime = DateTime.UtcNow;
      }
      catch (OperationCanceledException)
      {
        break; // Exit the loop if the operation is cancelled
      }

      await using (incomingRequest)
      {
        var requestWasRetried = false;
        var lcontext = incomingRequest?.Context;
        bool isExpired = false;

        ProxyEvent proxyEvent = new()
        {
            EventData = {
                ["PoxyHost"] = _options.HostName,
                ["Date"] = incomingRequest?.DequeueTime.ToString("o") ?? DateTime.UtcNow.ToString("o"),
                ["Path"] = incomingRequest?.Path ?? "N/A",
                ["x-RequestPriority"] = incomingRequest?.Priority.ToString() ?? "N/A",
                ["x-RequestMethod"] = incomingRequest?.Method ?? "N/A",
                ["x-RequestPath"] = incomingRequest?.Path ?? "N/A",
                ["x-RequestHost"] = incomingRequest?.Headers["Host"] ?? "N/A",
                ["x-RequestUserAgent"] = incomingRequest?.Headers["User-Agent"] ?? "N/A",
                ["x-RequestContentType"] = incomingRequest?.Headers["Content-Type"] ?? "N/A",
                ["x-RequestContentLength"] = incomingRequest?.Headers["Content-Length"] ?? "N/A",
                ["x-RequestWorker"] = IDstr
            }
        };
        var proxyEventData = proxyEvent.EventData;

        if (lcontext == null || incomingRequest == null)
        {
          continue;
        }
        var lcontextResponse = lcontext.Response;
        var lcontextResponseHeaders = lcontextResponse.Headers;
        var outputStream = lcontextResponse.OutputStream;
        try
        {
          if (incomingRequest.Path == "/health")
          {
            lcontextResponse.StatusCode = 200;
            lcontextResponse.ContentType = "text/plain";
            lcontextResponse.Headers.Add("Cache-Control", "no-cache");
            lcontextResponse.KeepAlive = false;

            var healthMessage = Encoding.UTF8.GetBytes(_backends?.HostStatus ?? "OK");
            lcontextResponse.ContentLength64 = healthMessage.Length;

            await outputStream.WriteAsync(
                healthMessage,
                0,
                healthMessage.Length,
                _cancellationToken).ConfigureAwait(false);
            continue;
          }
            var incomingHeaders = incomingRequest.Headers;
          incomingHeaders["x-Request-Queue-Duration"] = (incomingRequest.DequeueTime - incomingRequest.EnqueueTime).TotalMilliseconds.ToString();
          incomingHeaders["x-Request-Process-Duration"] = (DateTime.UtcNow - incomingRequest.DequeueTime).TotalMilliseconds.ToString();
          incomingHeaders["x-Request-Worker"] = IDstr;
          incomingHeaders["x-S7PID"] = incomingRequest.MID ?? "N/A";
          incomingHeaders["x-S7PPriority"] = incomingRequest.Priority.ToString() ?? "N/A";

          proxyEventData["x-Request-Queue-Duration"] = incomingHeaders["x-Request-Queue-Duration"] ?? "N/A";
          proxyEventData["x-Request-Process-Duration"] = incomingHeaders["x-Request-Process-Duration"] ?? "N/A";
          proxyEventData["x-S7PID"] = incomingRequest.MID ?? "N/A";

        var pr = await ReadProxyAsync(incomingRequest, _cancellationToken).ConfigureAwait(false);

          proxyEventData["x-Status"] = ((int)pr.StatusCode).ToString();
          if (_options.LogHeaders != null && _options.LogHeaders.Count > 0)
          {
            foreach (var header in _options.LogHeaders)
            {
              proxyEventData[header] = lcontextResponseHeaders[header] ?? "N/A";
            }
          }

          if (pr.StatusCode == HttpStatusCode.Gone) // 410 Gone
          {
            // Request has expired
            isExpired = true;
          }

          _logger.LogInformation($"Pri: {incomingRequest.Priority} Stat: {(int)pr.StatusCode} Len: {pr.ContentHeaders["Content-Length"]} {pr.FullURL}");

          proxyEventData["Url"] = pr.FullURL;
          proxyEventData["x-Response-Latency"] = (pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
          proxyEventData["x-Total-Latency"] = (DateTime.UtcNow - incomingRequest.EnqueueTime).TotalMilliseconds.ToString("F3");
          proxyEventData["x-Backend-Host"] = pr?.BackendHostname ?? "N/A";
          proxyEventData["x-Backend-Host-Latency"] = pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
          proxyEventData["Content-Length"] = lcontextResponse?.ContentLength64.ToString() ?? "N/A";
          proxyEventData["Content-Type"] = lcontextResponse?.ContentType ?? "N/A";

          if (_eventClient != null)
          {
            proxyEventData["Type"] = isExpired ? "S7P-Expired-Request" : "S7P-ProxyRequest";
            _eventClient.SendData(proxyEvent);
          }
        }
        catch (S7PRequeueException e)
        {
          // Requeue the request 
          _requestsQueue.Requeue(incomingRequest, incomingRequest.Priority, incomingRequest.EnqueueTime);
          _logger.LogInformation($"Requeued request.  Pri: {incomingRequest.Priority} Queue Length: {_requestsQueue.Count} Status: {_backends.CheckFailedStatus()} Active Hosts: {_backends.ActiveHostCount()}");
          requestWasRetried = true;
          incomingRequest.SkipDispose = true;
          proxyEventData["Url"] = e.Pr.FullURL;
          proxyEventData["x-Status"] = ((int)503).ToString();
          proxyEventData["x-Response-Latency"] = (e.Pr.ResponseDate - incomingRequest.DequeueTime).TotalMilliseconds.ToString("F3");
          proxyEventData["x-Total-Latency"] = (DateTime.UtcNow - incomingRequest.EnqueueTime).TotalMilliseconds.ToString("F3");
          proxyEventData["x-Backend-Host"] = e.Pr?.BackendHostname ?? "N/A";
          proxyEventData["x-Backend-Host-Latency"] = e.Pr?.CalculatedHostLatency.ToString("F3") ?? "N/A";
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
          proxyEventData["x-Status"] = "502";
          proxyEventData["Type"] = "S7P-IOException";
          proxyEventData["x-Message"] = ioEx.Message;

          _logger.LogError($"An IO exception occurred: {ioEx.Message}");
          lcontextResponse.StatusCode = 502;
          var errorMessage = Encoding.UTF8.GetBytes($"Broken Pipe: {ioEx.Message}");
          try
          {
            await outputStream.WriteAsync(
                errorMessage,
                0,
                errorMessage.Length,
                _cancellationToken).ConfigureAwait(false);
          }
          catch (Exception writeEx)
          {
            _logger.LogError($"Failed to write error message: {writeEx.Message}");
            proxyEventData["x-Status"] = "Network Error";
            _eventClient.SendData(proxyEvent);
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
            var message = ex.Message;
          if (message == "Cannot access a disposed object."
            || message.StartsWith("Unable to write data")
            || message.Contains("Broken Pipe")) // The client likely closed the connection
          {
            _logger.LogError($"Client closed connection: {incomingRequest.FullURL}");
            proxyEventData["x-Status"] = "Network Error";
            _eventClient.SendData(proxyEvent);
            continue;
          }
          // Log the exception
          _logger.LogError($"Exception: {message}");
          _logger.LogError($"Stack Trace: {ex.StackTrace}");

          // Set an appropriate status code for the error
          lcontextResponse.StatusCode = 500;
          var errorMessage = Encoding.UTF8.GetBytes("Internal Server Error");
          try
          {
            _telemetryClient?.TrackException(ex, proxyEvent.EventData);
            await outputStream.WriteAsync(
                errorMessage,
                0,
                errorMessage.Length,
                _cancellationToken).ConfigureAwait(false);
          }
          catch (Exception writeEx)
          {
            _logger.LogError($"Failed to write error message: {writeEx.Message}");
          }
        }
        finally
        {
          // Let's not track the request if it was retried.
          if (!requestWasRetried)
          {
            // Track the status of the request for circuit breaker
            _backends.TrackStatus(lcontextResponse.StatusCode);
            _telemetryClient?.TrackRequest($"{incomingRequest.Method} {incomingRequest.Path}",
                DateTimeOffset.UtcNow, new TimeSpan(0, 0, 0), $"{lcontextResponse.StatusCode}", true);
            //_telemetryClient?.TrackEvent("ProxyRequest", proxyEvent);

            lcontextResponse.Close();
          }
        }
      }
    }

    _logger.LogInformation($"Worker {IDstr} stopped.");
  }

    private Task WriteResponseDataAsync(HttpListenerContext context,
        ProxyData pr, CancellationToken token)
    {
        HttpListenerResponseWrapper listener = new(context.Response);
        return _proxyStreamWriter.WriteResponseDataAsync(listener, pr, token);
    }

    public async Task<ProxyData> ReadProxyAsync(RequestData request, CancellationToken cancellationToken) //DateTime requestDate, string method, string path, WebHeaderCollection headers, Stream body)//HttpListenerResponse downStreamResponse)
    {
        if (request == null) throw new ArgumentNullException(nameof(request), "Request cannot be null.");
        if (request.Body == null) throw new ArgumentNullException(nameof(request.Body), "Request body cannot be null.");
        if (request.Headers == null) throw new ArgumentNullException(nameof(request.Headers), "Request headers cannot be null.");
        if (request.Method == null) throw new ArgumentNullException(nameof(request.Method), "Request method cannot be null.");

        var activeHosts = _backends.GetActiveHosts();
        request.Debug = _debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));

        byte[] bodyBytes = await request.CacheBodyAsync().ConfigureAwait(false);
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
        async Task<ProxyData> WriteProxyDataAsync(ProxyData data, CancellationToken token)
        {
            var context = request.Context
                ?? throw new NullReferenceException("Request context cannot be null.");

            data.ResponseMessage = new HttpResponseMessage(HttpStatusCode.InternalServerError);

            await WriteResponseDataAsync(context, data, token);
            return data;
        }

        if (request.TTLSeconds == 0)
        {
            if (!CalculateTTL(request))
            {
                return await WriteProxyDataAsync(new()
                {
                    StatusCode = HttpStatusCode.BadRequest,
                    ResponseMessage = new(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("Invalid TTL format: " + request.Headers["S7PTTL"], Encoding.UTF8)
                    }
                }, cancellationToken);
            }
        }

        // Check ttlSeconds against current time .. keep in mind, client may have disconnected already
        if (request.TTLSeconds < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            HandleProxyRequestError(null, null, request.Timestamp, request.FullURL, HttpStatusCode.Gone, "Request has expired: " + DateTimeOffset.UtcNow.ToLocalTime());

            return await WriteProxyDataAsync(new()
            {
                StatusCode = HttpStatusCode.Gone,
                ResponseMessage = new(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("Invalid TTL format: " + request.Headers["S7PTTL"], Encoding.UTF8)
                }
            }, cancellationToken);
        }

        if (_options.UseOAuth)
        {
            // Get a token
            var token = _backends.OAuth2Token();
            if (request.Debug)
            {
                _logger.LogDebug("Token: " + token);
            }
            // Set the token in the headers
            request.Headers.Set("Authorization", $"Bearer {token}");
        }

        //TODO: Parallelize this
        foreach (var host in activeHosts)
        {
            await Task.Yield();
            try
            {
                request.Headers.Set("Host", host.Host);
                var urlWithPath = new UriBuilder(host.Url) { Path = request.Path }.Uri.AbsoluteUri;
                request.FullURL = WebUtility.UrlDecode(urlWithPath);

                using ByteArrayContent bodyContent = new(bodyBytes);
                using HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL);
                proxyRequest.Content = bodyContent;
                CopyHeaders(request.Headers, proxyRequest, true);

                if (bodyBytes.Length > 0)
                {
                    proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                    // Preserve the content type if it was provided
                    var contentType = request.Context?.Request.ContentType; // Default to application/octet-stream if not specified
                    var mediaTypeHeaderValue = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
                    // Preserve the encoding type if it was provided
                    if (contentType != null && contentType.Contains("charset"))
                    {
                        var charset = contentType.Split(';').LastOrDefault(s => s.Trim().StartsWith("charset"));
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
                    _logger.LogDebug($"> {request.Method} {request.FullURL} {bodyBytes.Length} bytes");
                    LogHeaders(proxyRequest.Headers, ">");
                    LogHeaders(proxyRequest.Content.Headers, "  >");
                }

                var proxyStartDate = DateTime.UtcNow;
                if(_options.Client == null) throw new NullReferenceException("Client cannot be null.");
                using var proxyResponse = await _options.Client.SendAsync(
                    proxyRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    _cancellationToken).ConfigureAwait(false);
                var responseDate = DateTime.UtcNow;
                lastStatusCode = proxyResponse.StatusCode;

                if (((int)proxyResponse.StatusCode > 300 && (int)proxyResponse.StatusCode < 400) || (int)proxyResponse.StatusCode >= 500)
                {
                    if (request.Debug)
                    {
                        try
                        {
                            //Why do this?
                            ProxyData temp_pr = new()
                            {
                                ResponseDate = responseDate,
                                StatusCode = proxyResponse.StatusCode,
                                FullURL = request.FullURL,
                            };
                            bodyBytes = [];
                            _logger.LogDebug($"Got: {temp_pr.StatusCode} {temp_pr.FullURL} {temp_pr.ContentHeaders["Content-Length"]} bytes");
                            _logger.LogDebug($"< {temp_pr?.Body}");
                        }
                        catch (Exception e)
                        {
                            _logger.LogError($"Error reading from backend host: {e.Message}");
                        }

                        _logger.LogInformation($"Trying next host: Response: {proxyResponse.StatusCode}");
                    }
                    continue;
                }

                host.AddPxLatency((responseDate - proxyStartDate).TotalMilliseconds);

                ProxyData pr = new()
                {
                    ResponseDate = responseDate,
                    StatusCode = proxyResponse.StatusCode,
                    ResponseMessage = proxyResponse,
                    FullURL = request.FullURL,
                    CalculatedHostLatency = host.CalculatedAverageLatency,
                    BackendHostname = host.Host
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

                var context = request.Context
                    ?? throw new NullReferenceException("Request context cannot be null.");
                // TODO: Move to caller to handle writing errors?
                await WriteResponseDataAsync(context, pr, cancellationToken);

                if (request.Debug)
                {
                    _logger.LogDebug($"Got: {pr.StatusCode} {pr.FullURL}");
                }
                return pr;
            }
            catch (S7PRequeueException)
            {
                // rethrow the exception
                //TODO: Figure out if this should write to the output stream.
                throw;
            }
            catch (TaskCanceledException)
            {
                // 408 Request Timeout
                lastStatusCode = HandleProxyRequestError(host, null, request.Timestamp, request.FullURL, HttpStatusCode.RequestTimeout, "Request to " + host.Url + " timed out");
                // log the stack trace 
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

        return await WriteProxyDataAsync(new()
        {
            StatusCode = HttpStatusCode.BadGateway,
            ResponseMessage = new(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("No active hosts were able to handle the request.", Encoding.UTF8)
            }
        }, cancellationToken);
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
                if (ttlString.StartsWith('+') && long.TryParse(ttlString[1..], out long longSeconds))
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
            _logger.LogInformation($"{prefix} {header.Key} : {string.Join(", ", header.Value)}");
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
}
