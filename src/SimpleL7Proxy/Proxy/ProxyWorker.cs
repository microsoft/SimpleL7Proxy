using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Llm;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.StreamProcessor;
using Shared.RequestAPI.Models;
using System.Collections.Frozen;
using SimpleL7Proxy.Tokenomics;

namespace SimpleL7Proxy.Proxy;



// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow

// The ProxyWorker class has the following main objectives:
// 1. Read incoming requests from the queue, prioritizing the highest priority requests.
// 2. Proxy the request to the backend with the lowest latency.
// 3. Retry against the next backend if the current backend fails.
// 4. Return a 502 Bad Gateway if all backends fail.
// 5. Return a 200 OK with backend server stats if the request is for /health.
// 6. Log telemetry data for each request.
public class ProxyWorker : IConfigChangeSubscriber
{
    private readonly WorkerContext _wrkCntxt;
    private readonly IEndpointMonitorService _backends;
    private readonly ProxyConfig _options;
    private readonly ILogger<ProxyRequestHandler> _logger;
    private readonly RequestLifecycleManager _lifecycleManager;
    private readonly StreamFlusher _streamFlusher;
    private readonly int _id;
    private CancellationTokenSource? _asyncExpelSource;
    private bool _isEvictingAsyncRequest;
    private static List<string> s_backendKeys = [];
    private static FrozenSet<string> s_stripRequestHeaders = FrozenSet.Create<string>();
    private static FrozenSet<string> s_stripResponseHeaders = FrozenSet.Create<string>();

    private bool detectModel = false;
    
    //private readonly ProxyStreamWriter _proxyStreamWriter;
    // private readonly string _timeoutHeaderName;

    // Static pre-allocated ProxyEvent objects for error scenarios to avoid expensive copy constructor
    // private static readonly ProxyEvent s_backendRequestAttemptEvent = new ProxyEvent(25);  // Base eventData (~20) + attempt fields (7)
    // private static readonly ProxyEvent s_finallyBlockErrorEvent = new ProxyEvent(18);

    internal bool IsEvictingAsyncRequest => _isEvictingAsyncRequest;

    public ProxyWorker(
        int id,
        WorkerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _wrkCntxt = context;
        _id = id;
        _options = context.BackendOptions;

        if (_options.Client == null) throw new ArgumentNullException(nameof(_options.Client));

        _backends = context.Backends;
        _logger = context.Logger;
        _lifecycleManager = context.LifecycleManager;
        _options = context.BackendOptions;
        _streamFlusher = context.StreamFlusher;

        InitVars();

        _wrkCntxt.ConfigChangeNotifier.Subscribe(
            this,
            // options => options.Workers,    COLD
            options => options.AsyncTimeout,
            options => options.AsyncTriggerTimeout,
            options => options.DependancyHeaders,
            options => options.DetectModel,
            options => options.IterationMode,
            options => options.LoadBalanceMode,
            options => options.MaxAttempts,
            options => options.StripRequestHeaders,
            options => options.StripResponseHeaders,
            options => options.Timeout,
            options => options.UseProfiles,
            options => options.UseSharedIterators
            );
    }

    /// <summary>
    /// Refreshes cached backend settings from the current configuration.
    /// </summary>
    public void InitVars() {
        s_backendKeys = _options.DependancyHeaders;
        s_stripRequestHeaders = _options.StripRequestHeaders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        s_stripResponseHeaders = _options.StripResponseHeaders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        detectModel = _options.DetectModel;
    }

    /// <summary>
    /// Refreshes cached backend settings after a configuration change.
    /// </summary>
    public Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        ProxyConfig backendOptions,
        CancellationToken cancellationToken) {
        InitVars();
        return Task.CompletedTask;
    }

    internal async Task WriteClientResponseAsync(RequestData request, ProxyData pr)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(request, "Request context is null.");

        var context = request.Context;

        // For async requests that triggered, the 202 Accepted response was already sent and
        // the connection was closed by AsyncWorker. Skip writing to the HttpListenerResponse.
        // The actual backend response will be streamed to blob storage in StreamResponseAsync.
        // For rehydrated/background-check requests Context is null by design — there is no
        // client connection to write headers to; the response goes only to blob storage.
        if (!request.AsyncTriggered && context != null)
        {
            // Set the response status code
            context.Response.StatusCode = (int)pr.StatusCode;

            // Add attempt counters to the client response. These can't be set on pr.Headers
            // earlier because CaptureResponseStream already copied pr.Headers into the response
            // by value, so late additions wouldn't propagate.
            context.Response.Headers["Attempts"] = request.BackendAttempts.ToString();
            context.Response.Headers["Lifetime-Attempts"] = request.LifetimeBackendAttempts.ToString();
            if (request.RequeueDelayMs > 0)
            {
                context.Response.Headers["Request-Requeue-Delay"] = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            }

            // These were also added to pr.Headers after the by-value copy in CaptureResponseStream,
            // so forward them here too. Read back from pr.Headers (single computation, consistent
            // with telemetry) and skip any that aren't present (e.g. on the error path).
            if (pr.Headers != null)
            {
                if (pr.Headers["BackendHost"] is { } backendHost) context.Response.Headers["BackendHost"] = backendHost;
                if (pr.Headers["Request-Queue-Duration"] is { } queueDuration) context.Response.Headers["Request-Queue-Duration"] = queueDuration;
                if (pr.Headers["Request-Process-Duration"] is { } processDuration) context.Response.Headers["Request-Process-Duration"] = processDuration;
                if (pr.Headers["Total-Latency"] is { } totalLatency) context.Response.Headers["Total-Latency"] = totalLatency;
                if (detectModel && request.Model is { Length: > 0 } model) request.EventData["Model"] = model;
                if (pr.Headers["x-backend-label"] is { } backendLabel) request.EventData["x-backend-label"] = backendLabel;
            }

            // Set content-specific headers
            if (pr.ContentHeaders != null)
            {
                foreach (var key in pr.ContentHeaders.AllKeys)
                {
                    switch (key.ToLower())
                    {
                        case "content-length":
                            var length = pr.ContentHeaders[key];
                            if (long.TryParse(length, out var contentLength))
                            {
                                context.Response.ContentLength64 = contentLength;
                            }
                            else
                            {
                                _logger.LogWarning("Invalid Content-Length: {Length}", length);
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
        }

        // we need 3 things:
        // 1. The processor to use                      => pr.StreamingProcessor
        // 2. The source stream (from backend)          => pr.BodyResponseMessage
        // 3. The destination stream (to client/blob)   => incomingRequest.OutputStream        

        // Stream response from backend to client/blob
        try
        {
            await StreamResponseAsync(request, pr).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[WriteResponseAsync:{Guid}] Error streaming response to {FullURL}",
                request.Guid, request.FullURL);
            throw;
        }

        try
        {
            _logger.LogDebug("[WriteResponseAsync:{Guid}] Flushing output stream", request.Guid);
            if (request.OutputStream != null)
            {
                await request.OutputStream.FlushAsync().ConfigureAwait(false);

                if (request.OutputStream is BufferedStream bufferedStream)
                {
                    await bufferedStream.FlushAsync().ConfigureAwait(false);
                }
            }
            _logger.LogDebug("[WriteResponseAsync:{Guid}] Output stream flushed successfully", request.Guid);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "[WriteResponseAsync:{Guid}] Unable to flush output stream", request.Guid);
        }
    }

    public void ExpelAsyncRequest()
    {
        if (_asyncExpelSource != null)
        {
            // Called during shutdown to evict any in-progress async requests ... then worker will exit
            _isEvictingAsyncRequest = true;
            _logger.LogDebug("Expelling async request in progress, cancelling the token.");
            try
            {
                _asyncExpelSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // CTS was already disposed - worker has finished or encountered an error
                _logger.LogDebug("AsyncExpelSource already disposed - worker has completed");
            }
        }

    }

    /// <summary>
    /// Routes a request to an available backend host using configured load balancing and iteration strategies.
    /// Iterates through matching hosts until one succeeds or all fail. Handles circuit breaker checks,
    /// OAuth token injection, request timeout management, and async worker coordination for long-running requests.
    /// </summary>
    /// <param name="request">The request containing body, headers, method, and execution mode flags (runAsync, IsBackground, IsBackgroundCheck)</param>
    /// <returns>ProxyData with response status, headers, content metadata, and backend hostname</returns>
    /// <exception cref="ArgumentNullException">When request, Body, Headers, or Method is null</exception>
    /// <exception cref="ProxyErrorException">When all hosts fail, request TTL expires (412), or no matching hosts found</exception>
    /// <exception cref="S7PRequeueException">When backend returns 429 with S7PREQUEUE header; includes retry-after delay</exception>
    /// <remarks>
    /// <para>For async requests (runAsync=true), creates AsyncWorker to write response to blob storage.
    /// Supports SinglePass (try each host once) and MultiPass (retry with MaxAttempts) iteration modes.</para>
    /// <code>
    /// ALGORITHM FLOW:
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  REQUEST ENTRY                                                          │
    /// │  ├─ Validate: Body, Headers, Method not null                            │
    /// │  └─ Create host iterator (SinglePass or MultiPass mode)                 │
    /// └───────────────────────────────┬─────────────────────────────────────────┘
    ///                                 ▼
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  FOR EACH HOST in iterator:                                             │
    /// │  ┌───────────────────────────────────────────────────────────────────┐  │
    /// │  │ 1. Circuit Breaker Check ──[OPEN]──► SKIP to next host            │  │
    /// │  │         │                                                         │  │
    /// │  │      [CLOSED]                                                     │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 2. TTL Check ──[EXPIRED]──► throw ProxyErrorException (412)       │  │
    /// │  │         │                                                         │  │
    /// │  │      [VALID]                                                      │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 3. OAuth Token? ──[YES]──► Inject Bearer token                    │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 4. Setup AsyncWorker (if runAsync) + CancellationToken            │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 5. SEND REQUEST ──────────────────────────────────────────────►   │  │
    /// │  │         │                                          [Backend]      │  │
    /// │  │         ◄─────────────────────────────────────────────────────    │  │
    /// │  │         │                                                         │  │
    /// │  │         ▼                                                         │  │
    /// │  │ 6. Response Status Check:                                         │  │
    /// │  │    ├─[3xx, 404, 412, 5xx]──► CONTINUE to next host                │  │
    /// │  │    ├─[429 + S7PREQUEUE]───► Collect for retry, CONTINUE           │  │
    /// │  │    └─[2xx SUCCESS]────────► Capture response, RETURN ProxyData    │  │
    /// │  └───────────────────────────────────────────────────────────────────┘  │
    /// └───────────────────────────────┬─────────────────────────────────────────┘
    ///                                 ▼
    /// ┌─────────────────────────────────────────────────────────────────────────┐
    /// │  ALL HOSTS EXHAUSTED:                                                   │
    /// │  ├─ If 429s collected ──► throw S7PRequeueException (shortest retry)    │
    /// │  └─ Else ──► throw ProxyErrorException (503 ServiceUnavailable)         │
    /// └─────────────────────────────────────────────────────────────────────────┘
    /// </code>
    /// </remarks>
    public async Task<ProxyData> ProxyToBackEndAsync(RequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Body, nameof(request.Body));
        ArgumentNullException.ThrowIfNull(request.Headers, nameof(request.Headers));
        ArgumentNullException.ThrowIfNull(request.Method, nameof(request.Method));

        _logger.LogDebug("[ProxyToBackEnd:{Guid}] Starting proxy attempt - Path: {Path}, Method: {Method}",
            request.Guid, request.Path, request.Method);

        List<Dictionary<string, string>> incompleteRequests = request.incompleteRequests;

        // request.Debug = s_debug || (request.Headers["S7PDEBUG"] != null && string.Equals(request.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase));
        HttpStatusCode lastStatusCode = HttpStatusCode.ServiceUnavailable;
        var requestSummary = request.EventData;
        int intCode = 0;
        bool ttlExpired = false;

        // Read the body stream once and reuse it
        //byte[] bodyBytes = await request.CachBodyAsync().ConfigureAwait(false);
        List<S7PRequeueException> retryAfter = new();

        var (iterator, iterationState, modifiedPath) = CreateHostIterator(request);

        // Use the host count from the already-created iterator (avoids redundant GetActiveHosts call
        // and fixes a bug where the old code compared stripped path against configured PartialPath)
        var matchingHostCount = iterator.HostCount;
        _logger.LogDebug("[ProxyToBackEnd:{Guid}] Found {HostCount} backend hosts for path {Path}",
            request.Guid, matchingHostCount, modifiedPath);

        if (matchingHostCount == 0 && request.Debug)
        {
            _logger.LogWarning("[ProxyToBackEnd:{Guid}] ⚠ NO BACKEND HOSTS matched path {Path} - Request will fail",
                request.Guid, modifiedPath);
            
            // Log all available hosts and their paths for debugging
            var activeHosts = _backends.GetActiveHosts();
            _logger.LogCritical("[ProxyToBackEnd:{Guid}] Available hosts and their paths:", request.Guid);
            foreach (var h in activeHosts)
            {
                var cbStatus = h.Config.GetCircuitBreakerStatusString();
                _logger.LogCritical("[ProxyToBackEnd:{Guid}]   - Host: {Host}, Path: {PartialPath}, CB-Status: {CBStatus}",
                    request.Guid, h.Host, h.Config.PartialPath, cbStatus);
            }
        }

        // Try the request on each active host, stop if it worked
        // Use helper method to abstract over shared vs per-request iterators

        BaseHostHealth? host;
        while (iterator.TryGet(iterationState, out host) && host != null)
        {
            DateTime proxyStartDate = DateTime.UtcNow;

            // track the number of attempts
            request.BackendAttempts++;
            request.LifetimeBackendAttempts++;
            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Attempting backend host: {Host} (Attempt #{Attempt})",
                request.Guid, host.Host, request.LifetimeBackendAttempts);
            bool SuccessfulRequest = false;
            bool TriggerHostCB = true;
            string requestState = "Init";
            HttpResponseHeaders? responseHeaders = null;
            // bool newcode = false;
            ProxyEvent requestAttempt = null!;

            requestAttempt = new ProxyEvent(request.EventData)
            {
                Type = EventType.BackendRequest,
                ParentId = request.ParentId,
                MID = $"{request.MID}-{request.LifetimeBackendAttempts}",
                Method = request.Method,
                ["Request-Date"] = proxyStartDate.ToString("o"),
                ["Backend-Host"] = host.Host,
                ["Host-URL"] = host.Url,
                ["Attempt"] = request.BackendAttempts.ToString(),
                ["Lifetime-Attempt"] = request.LifetimeBackendAttempts.ToString()
            };

            // Tracked as an attempt
            try
            {
                // if (request.Context?.Request.Url != null)
                //     requestAttempt.Uri = request.Context!.Request.Url!;
                // else
                requestAttempt.Uri = new Uri(modifiedPath);
                requestState = "Calc ExpiresAt";

                // Validate request hasn't expired
                _lifecycleManager.ValidateRequestNotExpired(request);  // throws ProxyErrorException

                var minDate = request.ExpiresAt < DateTime.UtcNow.AddMilliseconds(request.defaultTimeout)
                    ? request.ExpiresAt
                    : DateTime.UtcNow.AddMilliseconds(request.defaultTimeout);
                request.Timeout = (int)(minDate - DateTime.UtcNow).TotalMilliseconds;

                request.Headers.Set("Host", host.Hostname);
                request.FullURL = host.Config.BuildDestinationUrl(modifiedPath);

                requestState = "Cache Body";

                // Read the body stream once and reuse it
                ReadOnlyMemory<byte> bodyBytes;
                bool wasCached = false;
                try 
                {
                    bodyBytes = await request.CacheBodyAsync(out wasCached).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "[ProxyToBackEnd:{Guid}] Unable to read request body for {FullURL}", request.Guid, request.FullURL);
                    throw new S7PClientReadException("Unable to read request body: " + ex.Message, request, ex);
                }

                if (_wrkCntxt.TokenomicsHandler.doTokenomics && !wasCached && bodyBytes.Length > 0)
                {

                    if (request.Debug)
                    {
                        _logger.LogInformation("[ValidateModel:{Guid}] Detecting model in request body of {Length} bytes, override: {Override}",
                            request.Guid, bodyBytes.Length, request.Headers["S7P-Model-Override"] ?? "(none)");
                    }
        
                    // Check what Tokenomics wants to do before working on the request.
                    (ModelOverrideEnum actionOverride, string ModelName) = _wrkCntxt.TokenomicsHandler.ProcessRequest(request);
                    if ( ModelName == String.Empty)
                    {
                        actionOverride = ModelOverrideEnum.Override;
                        ModelName = request.Headers["S7P-Model-Override"] ?? String.Empty;
                    }

                    bodyBytes = ModelSwapper.ValidateModel(
                        request,
                        bodyBytes,
                        actionOverride,
                        ModelName,
                        _wrkCntxt.TokenomicsHandler);

                    if (request.Headers["S7PDEBUGBODY"] is {} debugBodyHeader && debugBodyHeader.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        var bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes.Span);
                        _logger.LogInformation("[ValidateModel:{Guid}] Request body after model validation: {BodyContent}",
                            request.Guid, bodyString);
                    }

                }
                

                if (request.runAsync &&
                    !request.AsyncTriggered &&
                    !request.Requeued &&
                    request.BackendAttempts == 1)
                {
                    requestState = "Persist Request Before Send";

                    // Persist the request as soon as the body has been materialized so
                    // rehydration still has the original payload if the process stops
                    // after the first backend send but before the async trigger fires.
                    var preSendAsyncWorker = request.asyncWorker;
                    if (preSendAsyncWorker == null)
                    {
                        var timeLeft = _options.AsyncTriggerTimeout - (int)(DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds;
                        timeLeft = Math.Max(1, timeLeft);
                        preSendAsyncWorker = new AsyncWorker(request, timeLeft, _wrkCntxt.AsyncWorkerContext!);
                        request.asyncWorker = preSendAsyncWorker;
                        _ = preSendAsyncWorker.StartAsync();
                    }

                    await preSendAsyncWorker.PersistRequestStateAsync().ConfigureAwait(false);
                }

                requestState = "Create Backend Request";

                var bodySegment = MemoryMarshal.TryGetArray(bodyBytes, out var segment) && segment.Array != null
                    ? segment
                    : new ArraySegment<byte>(bodyBytes.ToArray());

                using (ByteArrayContent bodyContent = new(bodySegment.Array!, bodySegment.Offset, bodySegment.Count))
                using (HttpRequestMessage proxyRequest = new(new(request.Method), request.FullURL))
                {
                    proxyRequest.Content = bodyContent;

                    // // Allow downgrade to HTTP/1.0 for backends that don't support HTTP/1.1.
                    // proxyRequest.Version = HttpVersion.Version11;
                    // proxyRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

                    proxyRequest.Headers.Add("x-PolicyCycleCounter", request.APIMPolicyCycleCounter.ToString());
                    proxyRequest.Headers.Add("x-LifetimePolicyCycleCounter", request.LifetimeAPIMPolicyCycleCounter.ToString());
                    proxyRequest.Headers.Add("x-LLMModel", request.Model);
                    ProxyHelperUtils.CopyHeaders(request.Headers, proxyRequest, true, s_stripRequestHeaders);

                    // add S7PDEBUG back in if it was requested
                    if ( request.Debug)
                    {
                        proxyRequest.Headers.Remove("S7PDEBUG");
                        proxyRequest.Headers.Add("S7PDEBUG", "True");
                    }

                    var contentType = request.Context?.Request.ContentType ?? "application/json";
                    if (!MediaTypeHeaderValue.TryParse(contentType, out var req_mediaType))
                    {
                        _logger.LogInformation("Invalid content type '{ContentType}', defaulting to application/json", contentType);
                        req_mediaType = new MediaTypeHeaderValue("application/json");
                    }
                    req_mediaType.CharSet ??= "utf-8";
                    proxyRequest.Content.Headers.ContentType = req_mediaType;

                    //if (bodyBytes.Length > 0)
                    //    proxyRequest.Content.Headers.ContentLength = bodyBytes.Length;

                    //proxyRequest.Headers.ConnectionClose = true;
                    switch (host.Config.AuthMode)
                    {
                        case AuthModeEnum.OAuth2:
                            // Get a token
                            var oaToken = await host.Config.OAuth2Token().ConfigureAwait(false);

                            // Set the token in the headers
                            proxyRequest.Headers.Authorization =
                                new AuthenticationHeaderValue("Bearer", oaToken);

                            break;
                        case AuthModeEnum.ApiKey:
                            // Set the API key in the headers
                            proxyRequest.Headers.Remove(host.Config.ApiKeyHeader);
                            proxyRequest.Headers.TryAddWithoutValidation(host.Config.ApiKeyHeader, host.Config.ApiKey);
                            break;
                    }

                    // Log request headers if debugging is enabled
                    if (request.Debug)
                    {
                        _logger.LogDebug("> {Method} {FullURL} {BodyLength} bytes",
                            request.Method, request.FullURL, bodyBytes.Length);
                        ProxyHelperUtils.LogHeaders(proxyRequest.Headers, ">", _logger);
                        ProxyHelperUtils.LogHeaders(proxyRequest.Content.Headers, "  >", _logger);
                        //string bodyString = System.Text.Encoding.UTF8.GetString(bodyBytes);
                        //Console.WriteLine($"Body Content: {bodyString}");
                    }

                    // Send the request and get the response
                    proxyStartDate = DateTime.UtcNow;
                    HealthCheckService.EnterState(_id, WorkerState.Sending);
                    try
                    {
                        // ASYNC: Calculate the timeout, start async worker
                        _isEvictingAsyncRequest = false;
                        requestState = "Backend Attempt ";

                        // Create ASYNC Worker if needed, and setup the timeout
                        // SEND THE REQUEST TO THE BACKEND USING THE APROPRIATE TIMEOUT.
                        // TO DO:   reuse the cts instead of creating a new one each time.
                        var (requestCts, rTimeout) = await SetupAsyncWorkerAndTimeout(request).ConfigureAwait(false);
                        DateTime responseDate;
                        using (requestCts)
                        {
                            HealthCheckService.EnterState(_id, WorkerState.Receiving);

                            // DO NOT ADD A USING BLOCK HERE - we need to process the response outside of this block

                            var proxyResponse = await _options.Client!.SendAsync(
                                proxyRequest, HttpCompletionOption.ResponseHeadersRead, requestCts.Token).ConfigureAwait(false);
                            responseHeaders = proxyResponse.Headers;
                            responseDate = DateTime.UtcNow;
                            lastStatusCode = proxyResponse.StatusCode;
                            requestAttempt.Status = proxyResponse.StatusCode;

                            var responseTimeToFirstByteMs = (responseDate - proxyStartDate).TotalMilliseconds;
                            host.TimeToFirstByteMs = responseTimeToFirstByteMs;

                            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Received response from {Host} - Status: {StatusCode}, Duration: {Duration}ms",
                                request.Guid, host.Host, lastStatusCode, responseTimeToFirstByteMs);

                            requestState = "Process Backend Response";

                            // Check if the status code of the response is in the set of allowed status codes, else try the next host
                            intCode = (int)proxyResponse.StatusCode;
                            var acceptableStatusCode = _options.AcceptableStatusCodes.Contains(intCode);
                            if (!acceptableStatusCode &&
                                ((intCode > 300 && intCode < 400) || intCode == 404 || intCode == 412 || intCode >= 500))
                            {
                                requestState = $"Backend proxy status code: {intCode}";

                                // 404 is not considered an error for circuit breaker purposes
                                if (intCode == 404)
                                    TriggerHostCB = false;

                                foreach (var header in proxyResponse.Headers)
                                {
                                    if (s_excludedHeaders.Contains(header.Key)) continue;
                                    requestAttempt[header.Key] = string.Join(", ", header.Value);
                                    //Console.WriteLine("requestAttempt[{0}] = {1}", header.Key, header.Value);
                                }

                                // The request did not succeed, try the next host
                                continue;
                            }

                            // Capture the response
                            ProxyData pr = new()
                            {
                                ResponseDate = responseDate,
                                StatusCode = lastStatusCode,
                                FullURL = request.FullURL,
                                CalculatedHostLatency = host.AverageLatencyMs,
                                BackendHostname = host.Host
                            };

                            host.AddProxyLatency((responseDate - proxyStartDate).TotalMilliseconds);

                            // Capture the response
                            try
                            {
                                // ASYNC: Synchronize with the asyncWorker to clean up output stream assignments:
                                // Either abort overriding the stream or be ready to write to the blob.
                                if (request.runAsync && request.asyncWorker != null && !await request.asyncWorker.Synchronize())
                                {
                                    _logger.LogWarning("[ProxyToBackEnd:{Guid}] AsyncWorker synchronization failed - Error: {Error}",
                                        request.Guid, request.asyncWorker.ErrorMessage);
                                    pr.Headers["x-Async-Error"] = request.asyncWorker.ErrorMessage;
                                    request.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessingError;
                                }

                                requestState = "Capture Proxy Response";

                                string resp_mediaType = proxyResponse.Content?.Headers?.ContentType?.MediaType ?? string.Empty;

                                _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Processor: {Processor}, MediaType: {MediaType}",
                                    request.Guid, pr.StreamingProcessor, resp_mediaType);

                                // Determine stream processor
                                pr.StreamingProcessor = host.Config.DirectMode
                                    ? host.Config.Processor
                                    : StreamProcessorFactory.DetermineStreamProcessor(proxyResponse, resp_mediaType);

                                requestState = $"{(host.Config.DirectMode ? "Direct Mode Processor" : "Stream Proxy Response")} : {pr.StreamingProcessor}";
                                await CaptureResponseStream(proxyResponse, request, pr).ConfigureAwait(false);
                                requestState = "Finalize Proxy Response";
                            }
                            finally
                            {
                                requestSummary["Backend-Host"] = pr.BackendHostname;
                                requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                                if (request.RequeueDelayMs > 0)
                                {
                                    requestSummary["Request-Requeue-Delay"] = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                                }
                                requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                                requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");
                            }


                            if (proxyResponse.Headers.TryGetValues("x-PolicyCycleCounter", out var apimPolicyAttempts))
                            {
                                if (int.TryParse(apimPolicyAttempts.FirstOrDefault(), out var apimPolicyCycleCounter))
                                {
                                    var apimPolicyCycleDelta = apimPolicyCycleCounter - request.APIMPolicyCycleCounter;
                                    request.LifetimeAPIMPolicyCycleCounter += apimPolicyCycleDelta;
                                    request.APIMPolicyCycleCounter = apimPolicyCycleCounter;
                                }
                            }

                            var (shouldRequeue, retryMs) = acceptableStatusCode
                                ? (false, 0)
                                : CheckRequeueResponse(proxyResponse, intCode, requestAttempt, ref requestState);

                            if (shouldRequeue)
                            {
                                throw new S7PRequeueException("Requeue request", retryMs);
                            }
                            else if (!acceptableStatusCode && intCode == 429)
                            {
                                // S7PREQUEUE was not "true" — capture backend response headers
                                // (e.g. backendLog, retry-after) into the attempt summary before
                                // trying the next host, matching the 3xx/4xx/5xx failure path.
                                foreach (var header in proxyResponse.Headers)
                                {
                                    if (s_excludedHeaders.Contains(header.Key)) continue;
                                    requestAttempt[header.Key] = string.Join(", ", header.Value);
                                }

                                continue;
                            }
                            else
                            {
                                // request was successful, so we can disable the skip
                                request.SkipDispose = false;
                                requestAttempt["RequestSuccess"] = "true"; // Track success in event data
                                bodyBytes = ReadOnlyMemory<byte>.Empty;
                            }

                            pr.Headers["BackendHost"] = requestSummary["Backend-Host"] = pr.BackendHostname;
                            pr.Headers["Request-Queue-Duration"] = requestSummary["Request-Queue-Duration"] = request.Headers["x-Request-Queue-Duration"] ?? "N/A";
                            if (request.RequeueDelayMs > 0)
                            {
                                var requeueDelay = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                                pr.Headers["Request-Requeue-Delay"] = requestSummary["Request-Requeue-Delay"] = requeueDelay;
                            }
                            pr.Headers["Request-Process-Duration"] = requestSummary["Request-Process-Duration"] = request.Headers["x-Request-Process-Duration"] ?? "N/A";
                            pr.Headers["Total-Latency"] = requestSummary["Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3");

                            // Strip headers from pr.Headers that are not allowed in the response
                            foreach (var header in s_stripResponseHeaders)
                            {
                                pr.Headers.Remove(header);
                            }

                            // Log the response if debugging is enabled
                            if (request.Debug)
                            {
                                _logger.LogDebug("Got: {StatusCode} {FullURL} {ContentLength} Body: {BodyLength} bytes",
                                    pr.StatusCode, pr.FullURL, pr.ContentHeaders["Content-Length"], pr?.Body?.Length);
                            }

                            SuccessfulRequest = true;
                            TriggerHostCB = false;
                            return pr ?? throw new ArgumentNullException(nameof(pr));
                        }   // closes the using on requestCts
                    }
                    finally
                    {
                        // State will be automatically cleaned up when entering next state
                        // or when worker shuts down via DecrementActiveWorkers
                    }
                }
            }
            catch (S7PClientReadException)
            {
                TriggerHostCB = false;
                intCode = (int)HttpStatusCode.BadRequest;
                throw;
            }
            catch (OutOfMemoryException oomEx)
            {
                TriggerHostCB = false;
                _logger.LogCritical(oomEx, "Out of memory caching request body for {Guid}", request.Guid);
                intCode = (int)HttpStatusCode.RequestEntityTooLarge; // 413
                throw new ProxyErrorException(
                    ProxyErrorException.ErrorType.ContentTooLarge,
                    HttpStatusCode.InternalServerError,
                    $"Request body too large to buffer: {oomEx.Message}");
            }
            catch (S7PRequeueException e)
            {
                if (e.now)
                    throw;

                TriggerHostCB = false;
                intCode = (int)HttpStatusCode.TooManyRequests; // 429
                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.TooManyRequests,
                    $"Requeue request: Retry-After = {e.RetryAfter}",
                    "Will retry if no other hosts are available");

                // Try all the hosts before sleeping
                retryAfter.Add(e);

                continue;
            }
            catch (ProxyErrorException e)
            {
                PopulateRequestAttemptError(requestAttempt, e.StatusCode, e.Message);
                intCode = (int)e.StatusCode;

                if (e.Type == ProxyErrorException.ErrorType.TTLExpired)
                {
                    ttlExpired = true;
                    intCode = 412;//(int)HttpResponseCode.PreconditionFailed; // 412
                    lastStatusCode = HttpStatusCode.PreconditionFailed;
                    TriggerHostCB = false;

                    break;
                }

                continue;
            }
            catch (TaskCanceledException) when (_isEvictingAsyncRequest)
            {
                TriggerHostCB = false;
                _logger.LogWarning("[Worker:{Id}] Request {Guid} was intentionally expelled.", _id, request.Guid);
                // Handle async expel case - request being evicted from memory
                if (request.asyncWorker != null)
                {
                    request.asyncWorker.ShouldReprocess = true;
                }

                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.ServiceUnavailable,
                    "Request being expelled",
                    "Request will rehydrate on startup");

                throw;
            }
            catch (TaskCanceledException)
            {
                // CTS timer fired — genuine backend timeout
                TriggerHostCB = false;
                intCode = (int)HttpStatusCode.RequestTimeout; // 408
                PopulateTimeoutError(requestAttempt, request, proxyStartDate);
                requestAttempt["Error"] = "Request Timed out";
                requestAttempt["Message"] = "Operation TIMEOUT";
                continue;
            }
            catch (OperationCanceledException)
            {
                // Explicit .Cancel() call (e.g. shutdown) — not a timeout
                TriggerHostCB = false;
                intCode = (int)HttpStatusCode.RequestTimeout; // 408
                PopulateTimeoutError(requestAttempt, request, proxyStartDate);
                requestAttempt["Error"] = "Request Cancelled";
                requestAttempt["Message"] = "Operation CANCELLED";
                continue;
            }
            catch (HttpRequestException e)
            {
                HttpStatusCode statusCode = ResolveHttpRequestErrorStatus(e);
                intCode = (int)statusCode;

                PopulateRequestAttemptError(requestAttempt, statusCode,
                    $"Bad Request: {e.Message}",
                    "Operation Exception: HttpRequest");

                requestState += $", statusCode = {statusCode}, HTTP Error Message: {e.Message}";
                continue;
            }
            catch (Exception e)
            {
                TriggerHostCB = false;

                if (IsInvalidHeaderException(e))
                {
                    throw new ProxyErrorException(ProxyErrorException.ErrorType.InvalidHeader,
                        HttpStatusCode.BadRequest, $"Bad header: {e.Message}");
                }
                
                // 500 Internal Server Error
                _logger.LogError(e, "Internal server error processing request {Guid} to {FullURL}",
                    request.Guid, request.FullURL);

                PopulateRequestAttemptError(requestAttempt, HttpStatusCode.InternalServerError,
                    $"Internal Error: {e.Message}");

                intCode = (int)HttpStatusCode.InternalServerError;
                requestState += ", Internal Error: " + e.Message;

                continue;
            }
            finally
            {
                // Add the request attempt to the summary
                requestAttempt.Duration = DateTime.UtcNow - proxyStartDate;
                requestAttempt.SendEvent();  // Log the dependent request attempt
                
                // Record result for the iterator (shared or per-request) and for the
                // iteration state's attempt count, which enforces MaxAttempts / the shared circular cap.
                iterator.RecordResult(iterationState, host, SuccessfulRequest);

                // Track host status for circuit breaker. 429 is normally excluded (a rate-limited
                // backend behind APIM isn't necessarily unhealthy), but a direct-mode host returning
                // 429 with a Retry-After header IS specifically that host telling us to back off it.
                var isDirectModeRateLimit = intCode == 429 && host.Config.DirectMode &&
                    responseHeaders != null &&
                    (responseHeaders.Contains("Retry-After") || responseHeaders.Contains("Retry-After-Ms"));

                if (!_isEvictingAsyncRequest && intCode != 412 && (intCode != 429 || isDirectModeRateLimit))
                    host.Config.TrackStatus(intCode, TriggerHostCB, "Attempt-" + request.LifetimeBackendAttempts, responseHeaders);

                if (!SuccessfulRequest)
                {
                    var miniDict = requestAttempt.ToDictionary(s_backendKeys);
                    miniDict["State"] = requestState;
                    incompleteRequests.Add(miniDict);

                    _logger.LogDebug(JsonSerializer.Serialize(miniDict));
                }

            }

            // continue to next host

        }

        // all hosts exhausted

        // If we get here, then no hosts were able to handle the request

        // A route-level maxattempts= (if any) was already resolved into the iteration state's value.
        // Uses LifetimeBackendAttempts (not the per-cycle BackendAttempts, which the requeue worker
        // resets to 0) so MaxAttempts is a true ceiling across requeue cycles.
        var effectiveMaxAttemptsForError = iterationState.MaxAttempts;
        var maxAttemptsReached = !ttlExpired &&
            request.IterationMode == IterationModeEnum.MultiPass &&
            effectiveMaxAttemptsForError > 0 &&
            request.LifetimeBackendAttempts >= effectiveMaxAttemptsForError;

        if (!maxAttemptsReached && retryAfter.Count > 0)
        {
            // If we have retry after values, return the smallest one
            var exc = retryAfter.MinBy(x => x.RetryAfter);
            if (exc != null)
            {
                // NOTE:  this throws an S7PRequeueException which is caught in the main loop
                throw exc;
            }
        }

        StringBuilder sb;
        bool statusMatches;
        int currentStatusCode;
        ProxyHelperUtils.GenerateErrorMessage(incompleteRequests, out sb, out statusMatches, out currentStatusCode);

        string errorDetail;
        if (maxAttemptsReached)
        {
            lastStatusCode = HttpStatusCode.PreconditionFailed;
            errorDetail = $"Maximum backend attempts reached ({effectiveMaxAttemptsForError}).";
        }
        else
        {
            // 502 Bad Gateway or the common status code when all attempts match
            lastStatusCode = statusMatches ? (HttpStatusCode)currentStatusCode : HttpStatusCode.BadGateway;
            errorDetail = ttlExpired
                ? "Request TTL expired."
                : incompleteRequests.Count == 0
                    ? "No backend attempts were completed."
                    : statusMatches
                        ? $"All backend attempts returned HTTP {currentStatusCode}."
                        : "Backends returned mixed status codes.";
        }
        var errorMessage = $"No active hosts were able to handle the request: {errorDetail}";
        sb.Insert(0, errorMessage + Environment.NewLine);
        // requestSummary.Type = EventType.ProxyError;

        // ASYNC: Synchronize with AsyncWorker if it was started, even for error responses
        // This ensures the 202 response was sent to client and blob streams are ready
        if (request.runAsync && request.asyncWorker != null)
        {
            _logger.LogDebug("[ProxyToBackEnd:{Guid}] Synchronizing with AsyncWorker before writing error response", request.Guid);
            if (!await request.asyncWorker.Synchronize())
            {
                _logger.LogWarning("[ProxyToBackEnd:{Guid}] AsyncWorker synchronization failed - Error: {Error}",
                    request.Guid, request.asyncWorker.ErrorMessage);
                // AsyncWorker failed to start, so AsyncTriggered will be false
                // Error response will go to HTTP context instead of blob
            }
            else
            {
                _logger.LogDebug("[ProxyToBackEnd:{Guid}] AsyncWorker synchronized successfully for error response", request.Guid);
            }
        }

        var errorBodyStr = sb.ToString();
        var errorBytes = Encoding.UTF8.GetBytes(errorBodyStr);
        var recordedStatusCode = ProxyHelperUtils.RecordIncompleteRequests(requestSummary, lastStatusCode, errorMessage, incompleteRequests);

        if (request.AsyncTriggered)
        {
            // TODO: Unify async error path to flow through WriteResponseAsync/StreamResponseAsync
            // like the non-async path below. For now, write directly via WriteExhaustedHostsErrorAsync.
            await WriteExhaustedHostsErrorAsync(request, lastStatusCode, errorBodyStr).ConfigureAwait(false);

            return new ProxyData
            {
                FullURL = request.FullURL,
                CalculatedHostLatency = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds,
                BackendHostname = "No Active Hosts Available",
                ResponseDate = DateTime.UtcNow,
                StatusCode = recordedStatusCode,
                Body = errorBytes,
            };
        }

        // Non-async path: build a ProxyData that flows through the normal
        // WriteResponseAsync → StreamResponseAsync pipeline, so error responses
        // use the same code path as success responses.
        var errorContent = new ByteArrayContent(errorBytes);
        errorContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        errorContent.Headers.ContentLength = errorBytes.Length;

        var errorHeaders = new WebHeaderCollection
        {
            ["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
            ["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
            ["x-ProxyHost"] = _options.HostName,
            ["x-MID"] = request.MID,
            ["Attempts"] = request.BackendAttempts.ToString(),
            ["Lifetime-Attempts"] = request.LifetimeBackendAttempts.ToString(),
            ["Model"] = request.Model
        };
        if (request.RequeueDelayMs > 0)
        {
            errorHeaders["Request-Requeue-Delay"] = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        }

        var errorResponse = new HttpResponseMessage(lastStatusCode) { Content = errorContent };

        // Set headers on the HTTP context so WriteResponseAsync finds them already applied
        if (request.Context != null)
        {
            request.Context.Response.StatusCode = (int)lastStatusCode;
            request.Context.Response.Headers = errorHeaders;
        }

        return new ProxyData
        {
            FullURL = request.FullURL,
            CalculatedHostLatency = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds,
            BackendHostname = "No Active Hosts Available",
            ResponseDate = DateTime.UtcNow,
            StatusCode = recordedStatusCode,
            Body = errorBytes,
            BodyResponseMessage = errorResponse,
            StreamingProcessor = StreamProcessorFactory.DEFAULT_PROCESSOR,
            Headers = errorHeaders,
            ContentHeaders = new WebHeaderCollection
            {
                ["Content-Type"] = "application/json; charset=utf-8",
                ["Content-Length"] = errorBytes.Length.ToString()
            }
        };
    }


    private async Task CaptureResponseStream(HttpResponseMessage proxyResponse, RequestData request, ProxyData pr)
    {
        if (request.Debug)
        {
            _logger.LogInformation("< " + request.Guid);
            foreach (var header in proxyResponse.Headers)
            {
                _logger.LogInformation("  < {Key}: {Value}", header.Key, string.Join(", ", header.Value));
            }
        }

        // copy headers from the response to the ProxyData object
        ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr, s_stripResponseHeaders);
        pr.BodyResponseMessage = proxyResponse;

        // // HTTP/1.0 backends use connection-close to delimit the response body (no Content-Length,
        // // no chunked encoding). The backend closes the TCP connection immediately after sending the
        // // body. Because we use ResponseHeadersRead, the body is read lazily — by the time
        // // StreamResponseAsync calls CopyToAsync, the socket may have been reclaimed by the
        // // connection pool, causing a SocketException.  Eagerly buffer the entire body here,
        // // while the socket is still open and attributed to this request.
        // if (proxyResponse.Version == HttpVersion.Version10 &&
        //     proxyResponse.Content.Headers.ContentLength == null)
        // {
        //     _logger.LogDebug("[CaptureResponseStream:{Guid}] HTTP/1.0 backend with no Content-Length detected — eagerly buffering body", request.Guid);
        //     await proxyResponse.Content.LoadIntoBufferAsync().ConfigureAwait(false);
        // }

        // ASYNC: If the request was triggered asynchronously, we need to write the response to the async worker blob
        // For background checks, skip header writing here - will be written in StreamResponseAsync if completed

        if (request.AsyncTriggered && !request.IsBackgroundCheck)
        {
            _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Writing headers to AsyncWorker blob", request.Guid);
            if (!await request.asyncWorker!.SaveResponseHeadersAsync(proxyResponse.StatusCode, pr.Headers))
            {
                throw new ProxyErrorException(ProxyErrorException.ErrorType.AsyncWorkerError,
                                            HttpStatusCode.InternalServerError, "Failed to write headers to async worker");
            }
            _logger.LogDebug("[GetProxyResponseAsync:{Guid}] Headers written successfully to AsyncWorker", request.Guid);
        }
        else if (!request.AsyncTriggered)
        {
            request.Context!.Response.StatusCode = (int)proxyResponse.StatusCode;
            request.Context.Response.Headers = pr.Headers;
        }

        return;
    }


    private void PopulateRequestAttemptError(
        ProxyEvent requestAttempt,
        HttpStatusCode status,
        string error,
        string? message = null)
    {
        requestAttempt.Status = status;
        requestAttempt["Error"] = error;
        if (message != null)
            requestAttempt["Message"] = message;
    }

    /// <summary>
    /// Creates a host iterator for routing requests to backend hosts, paired with the
    /// per-request <see cref="IterationState"/> that <see cref="IHostIterator"/> uses for
    /// pass/repeat control (SinglePass vs MultiPass, MaxAttempts) and circuit-breaker skip/
    /// all-open handling. Uses shared iterators (fair distribution across concurrent requests)
    /// or per-request iterators based on configuration.
    /// </summary>
    /// <returns>The ready-to-use iterator, its per-request state, and the modified path for this request.</returns>
    private (IHostIterator iterator, IterationState state, string modifiedPath) CreateHostIterator(RequestData request)
    {
        string modifiedPath = "";
        IHostIterator iterator;
        int maxAttempts = _options.MaxAttempts;
        var iterationMode = request.IterationMode;

        var routeMatch = _backends.MatchRoute(request.Path);
        var usesPriorityRouting = routeMatch != null ||
            _backends.GetHosts().Any(host =>
                host.Config.PriorityGroup != 1 || host.Config.AcceptablePriorities.Count > 0);

        // Latency/TTFB re-rank hosts per request using live health metrics — a shared iterator
        // freezes the order at first creation and cycles it circularly forever, silently
        // degrading them to round-robin. Only fairness-only, order-agnostic modes may share.
        var loadBalanceModeSupportsSharing =
            _options.LoadBalanceMode is Constants.RoundRobin or Constants.Random;

        if (_options.UseSharedIterators && _wrkCntxt.SharedIteratorRegistry != null &&
            !usesPriorityRouting && loadBalanceModeSupportsSharing)
        {
            // Use shared iterator - multiple requests to same path share the same iterator
            // The modifiedPath is stored on the iterator itself, so we don't need a second filtering call
            var sharedIterator = _wrkCntxt.SharedIteratorRegistry.GetOrCreate(
                request.Path,
                () => IteratorFactory.CreateSharedHostSnapshot(
                    _backends,
                    _options.LoadBalanceMode,
                    request.Path,
                    request.Priority));

            // Read modifiedPath from the shared iterator (computed once, cached)
            modifiedPath = sharedIterator.ModifiedPath;
            iterator = sharedIterator;

            _logger.LogDebug(
                "[ProxyToBackEnd:{Guid}] Using SHARED iterator for path '{Path}' with {HostCount} hosts",
                request.Guid, request.Path, sharedIterator.HostCount);
        }
        else if (routeMatch != null)
        {
            // Named Path_* route matched — honor its configured host list and order directly,
            // regardless of LoadBalanceMode (the route's "hosts=" order is the explicit intent).
            var candidateHosts = routeMatch.Value.Route.GetCandidateHosts(request.Priority);
            modifiedPath = routeMatch.Value.ModifiedPath;

            // A route-level maxattempts= overrides the global MaxAttempts option when set.
            maxAttempts = routeMatch.Value.Route.MaxAttempts ?? _options.MaxAttempts;

            // A valid per-request header remains the most specific override. Otherwise,
            // use the route mode when configured, then fall back to the global mode.
            var iterationModeHeader = request.Headers["S7P-Iterator"].AsSpan().Trim();
            var hasRequestModeOverride =
                Enum.TryParse(iterationModeHeader, true, out IterationModeEnum requestModeOverride) &&
                requestModeOverride is IterationModeEnum.SinglePass or IterationModeEnum.MultiPass;
            if (!hasRequestModeOverride && routeMatch.Value.Route.IterationMode.HasValue)
            {
                iterationMode = routeMatch.Value.Route.IterationMode.Value;
                request.IterationMode = iterationMode;
            }

            iterator = IteratorFactory.CreateFixedOrderIterator(candidateHosts);

            _logger.LogDebug(
                "[ProxyToBackEnd:{Guid}] Using named route '{RouteName}' for path '{Path}' with {HostCount} hosts (IterationMode={IterationMode}, MaxAttempts={MaxAttempts})",
                request.Guid, routeMatch.Value.Route.Name, request.Path, iterator.HostCount, iterationMode, maxAttempts);
        }
        else
        {
            // Use per-request iterator (original behavior). The ordering iterator no longer
            // cares about SinglePass vs MultiPass — NextHost owns that.
            iterator = IteratorFactory.CreateSinglePassIterator(
                _backends,
                _options.LoadBalanceMode,
                request.Path,
                request.Priority,
                out modifiedPath);
        }

        // Seeded with LifetimeBackendAttempts so MaxAttempts is a true ceiling across
        // requeue cycles, not just the current one.
        var state = new IterationState(iterationMode, maxAttempts, logger: _logger, priorAttempts: request.LifetimeBackendAttempts);

        return (iterator, state, modifiedPath);
    }

    /// <summary>
    /// Maps an HttpRequestException to the most appropriate HTTP status code by inspecting
    /// the exception's StatusCode, inner SocketException error codes, and error message text.
    /// </summary>
    private static HttpStatusCode ResolveHttpRequestErrorStatus(HttpRequestException e)
    {
        HttpStatusCode statusCode = e.StatusCode ?? HttpStatusCode.BadGateway;

        if (e.StatusCode != null)
            return statusCode;

        // Infer from inner SocketException
        if (e.InnerException is SocketException socketEx)
        {
            switch (socketEx.SocketErrorCode)
            {
                case SocketError.HostNotFound:
                case SocketError.TryAgain:
                case SocketError.NoData:
                    return HttpStatusCode.ServiceUnavailable; // 503
                case SocketError.TimedOut:
                    return HttpStatusCode.RequestTimeout; // 408
                case SocketError.ConnectionRefused:
                    return HttpStatusCode.BadGateway; // 502
            }
        }

        // Fallback to message parsing
        if (statusCode == HttpStatusCode.BadGateway)
        {
            if (e.Message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Name resolution failed", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.ServiceUnavailable; // 503
            }
            else if (e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return HttpStatusCode.RequestTimeout; // 408
            }
        }

        return statusCode;
    }

    /// <summary>
    /// Checks whether a 429 response with the S7PREQUEUE header should trigger a requeue.
    /// Copies response headers into the request attempt event and parses retry-after timing.
    /// </summary>
    /// <returns>(shouldRequeue: true if S7PREQUEUE="true", retryMs: delay before requeue)</returns>
    internal static (bool shouldRequeue, int retryMs) CheckRequeueResponse(
        HttpResponseMessage proxyResponse,
        int intCode,
        ProxyEvent requestAttempt,
        ref string requestState)
    {
        if (intCode != 429 || !proxyResponse.Headers.TryGetValues("S7PREQUEUE", out var values))
            return (false, 0);

        requestState = "Process 429";

        foreach (var header in proxyResponse.Headers.ToList())
        {
            if (s_excludedHeaders.Contains(header.Key)) continue;
            requestAttempt[header.Key] = string.Join(", ", header.Value);
        }

        if (!string.Equals(values.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase))
            return (false, 0);

        // Try retry-after-ms (milliseconds), then retry-after (seconds), default to 1000ms
        int retryMs = 1000;
        if (proxyResponse.Headers.TryGetValues("retry-after-ms", out var retryAfterValuesMS) &&
            int.TryParse(retryAfterValuesMS.FirstOrDefault(), out var retryAfterValueMS))
        {
            retryMs = retryAfterValueMS;
        }
        else if (proxyResponse.Headers.TryGetValues("retry-after", out var retryAfterValues) &&
                 int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterValue))
        {
            retryMs = retryAfterValue * 1000;
        }

        return (true, retryMs);
    }

    /// <summary>
    /// Writes the error response when all backend hosts have been exhausted.
    /// Routes to blob storage (for async requests) or HTTP context (for sync requests).
    /// </summary>
    private async Task WriteExhaustedHostsErrorAsync(RequestData request, HttpStatusCode statusCode, string errorBody)
    {
        try
        {
            if (request.AsyncTriggered && request.asyncWorker != null)
            {
                _logger.LogInformation("Writing error response to AsyncWorker blob for request {Guid} - Status: {StatusCode}",
                    request.Guid, statusCode);

                var errorHeaders = new WebHeaderCollection
                {
                    ["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms",
                    ["x-ProxyHost"] = _options.HostName,
                    ["x-MID"] = request.MID,
                    ["Attempts"] = request.BackendAttempts.ToString(),
                    ["Lifetime-Attempts"] = request.LifetimeBackendAttempts.ToString()
                };
                if (request.RequeueDelayMs > 0)
                {
                    errorHeaders["Request-Requeue-Delay"] = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                }

                await request.asyncWorker.SaveResponseHeadersAsync(statusCode, errorHeaders);

                var errorBytes = Encoding.UTF8.GetBytes(errorBody);
                if (request.IsBackgroundCheck)
                {
                    var outputStream = await request.asyncWorker.GetResponseDataStreamAsync();
                    await outputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                    await outputStream.FlushAsync().ConfigureAwait(false);
                }
                else if (request.OutputStream != null)
                {
                    await request.OutputStream.WriteAsync(errorBytes).ConfigureAwait(false);
                    await request.OutputStream.FlushAsync().ConfigureAwait(false);
                }
            }
            else if (!request.AsyncTriggered && request.Context != null)
            {
                _logger.LogInformation("Response Status Code: '{StatusCode}' for request {Guid}", statusCode, request.Guid);
                request.Context.Response.StatusCode = (int)statusCode;
                request.Context.Response.KeepAlive = false;

                request.Context.Response.Headers["x-Request-Queue-Duration"] = (request.DequeueTime - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                if (request.RequeueDelayMs > 0)
                {
                    request.Context.Response.Headers["x-Request-Requeue-Delay"] = request.RequeueDelayMs.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                }
                request.Context.Response.Headers["x-Total-Latency"] = (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds.ToString("F3") + " ms";
                request.Context.Response.Headers["x-ProxyHost"] = _options.HostName;
                request.Context.Response.Headers["x-MID"] = request.MID;
                request.Context.Response.Headers["Attempts"] = request.BackendAttempts.ToString();
                request.Context.Response.Headers["Lifetime-Attempts"] = request.LifetimeBackendAttempts.ToString();
                request.Context.Response.Headers["Model"] = request.Model;

                await request.Context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(errorBody)).ConfigureAwait(false);
                await request.Context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning("Cannot write error response for request {Guid} - Context: {HasContext}, AsyncTriggered: {AsyncTriggered}, AsyncWorker: {HasAsyncWorker}",
                    request.Guid, request.Context != null, request.AsyncTriggered, request.asyncWorker != null);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error writing error response for request {Guid} - AsyncTriggered: {AsyncTriggered}",
                request.Guid, request.AsyncTriggered);
        }
    }

    private void PopulateTimeoutError(
        ProxyEvent requestAttempt,
        RequestData request,
        DateTime proxyStartDate)
    {
        requestAttempt.Status = HttpStatusCode.RequestTimeout;
        requestAttempt["Expires-At"] = request.ExpiresAt.ToString("o");
        requestAttempt["MaxTimeout"] = _options.Timeout.ToString();
        requestAttempt["Request-Date"] = proxyStartDate.ToString("o");
        requestAttempt["Request-Timeout"] = $"{request.Timeout} ms";
    }

    private static bool IsInvalidHeaderException(Exception ex)
        => ex.Message.StartsWith("The format of value");

    /// <summary>
    /// Streams the response content from the backend to the client using the appropriate stream processor.
    /// 
    /// RESPONSE ROUTING LOGIC:
    ///   - Synchronous mode: Streams directly to request.OutputStream (client HTTP connection)
    ///   - Async/Background mode: Streams to asyncWorker which writes to blob storage
    /// 
    /// STREAM PROCESSORS:
    ///   - DefaultStream: Pass-through streaming with no processing
    ///   - OpenAI: Detects batch IDs and triggers background processing mode
    ///   - AllUsage: Extracts and logs usage information from OpenAI responses
    ///   - MultiLineAllUsage: Handles multi-line usage data extraction
    /// 
    /// BACKGROUND MODE DETECTION:
    ///   When processor.BackgroundCompleted is set, this indicates a background batch job
    ///   was initiated (e.g., OpenAI batch API). The request will transition to background
    ///   polling mode and status will be tracked separately.
    /// </summary>
    /// <param name="request">The incoming request data</param>
    /// <param name="proxyResponse">The HTTP response from the backend</param>
    /// <param name="processWith">The name of the processor to use for streaming</param>
    private async Task StreamResponseAsync(RequestData request, ProxyData pr)
    {
        //ProxyEvent requestSummary = request.EventData;
        string processWith = pr.StreamingProcessor ?? StreamProcessorFactory.DEFAULT_PROCESSOR;
        var proxyResponse = pr.BodyResponseMessage;

        if (proxyResponse == null)
        {
            _logger.LogError("Null Proxy response: Guid: {Guid}", request.Guid);
            return;
        }

        IStreamProcessor processor = _wrkCntxt.StreamProcessorFactory.GetStreamProcessor(processWith, out string resolvedProcessor);
        MemoryStream? memoryBuffer = null;
        
        try
        {
            _logger.LogDebug("Resolved processor: {ProcessorName} for request {Guid}", resolvedProcessor, request.Guid);

            // Route response to appropriate destination based on execution mode
            Stream? destination;
            bool needsFlush = false;
            string destinationType;

            if (request.IsBackgroundCheck && request.asyncWorker != null)
            {
                destinationType = "memory buffer";
                memoryBuffer = new MemoryStream();
                destination = memoryBuffer;             // <-- track this in memory for background checks
            }
            else if (request.runAsync && request.asyncWorker != null)
            {
                destinationType = "async blob";
                needsFlush = true;                      // QueuedBlobStream requires FlushAsync to enqueue data
                destination = await request.asyncWorker.GetResponseDataStreamAsync().ConfigureAwait(false);
            }
            else if (request.OutputStream != null)
            {
                destinationType = "client";
                destination = request.OutputStream;
            }
            else
            {
                _logger.LogError("OutputStream is null for request {Guid}, cannot stream response", request.Guid);
                return;
            }

            if (proxyResponse.Content != null)
            {
                _logger.LogDebug("Streaming to {Destination} for request {Guid}", destinationType, request.Guid);

                var addedToFlusher = _streamFlusher.AddStream(destination);
                var debugStream = request.Headers["S7PDEBUGSTREAM"] is {} debugValue && debugValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                await processor.CopyToAsync(proxyResponse.Content, destination, debugStream).ConfigureAwait(false);
                if (addedToFlusher)
                {
                    _streamFlusher.RemoveStream(destination);
                }
            
                if (needsFlush)
                {
                    await destination.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (HttpListenerException ex)
        {
            _logger.LogDebug(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-HttpListener | Guid: {Guid} | Error: {ErrorMessage}", 
                request.Guid, ex.Message);
        }
        catch (Exception ex) when (ex is IOException || ex.InnerException is IOException)
        {
            _logger.LogDebug(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-IO | Guid: {Guid} | Error: {ErrorMessage}", 
                request.Guid, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BLOB-TRACE] StreamResponseAsync | Action: Error-General | Guid: {Guid} | Error: {ErrorMessage} | Type: {ExType}",
                request.Guid, ex.Message, ex.GetType().FullName);
            // throw new ProxyErrorException(
            //     ProxyErrorException.ErrorType.ClientDisconnected,
            //     HttpStatusCode.InternalServerError,
            //     $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (request.IsBackgroundCheck && request.asyncWorker != null && memoryBuffer != null && processor != null)
                {
                    await HandleBackgroundCheckResultAsync(request, proxyResponse, processor, memoryBuffer);
                }

                if (proxyResponse.Headers != null && processor != null)
                {
                    processor.GetStats(request.EventData, proxyResponse.Headers);

                    // submit stats to Tokenomics
                    if (_wrkCntxt.TokenomicsHandler.doTokenomics
                        && !string.IsNullOrWhiteSpace(request.UserID)
                        && !string.IsNullOrWhiteSpace(request.Model))
                    {
                        // Pull the Tokenomics-relevant stats that GetStats just populated into
                        // request.EventData, ready for the upcoming submission to Tokenomics.
                        var isJailbreakDetected = request.EventData.TryGetValue("Usage.Is_Jailbreak_Detected", out var jailbreakDetectedStr)
                            && bool.TryParse(jailbreakDetectedStr, out var jailbreakDetectedValue) && jailbreakDetectedValue;
                        var isContentFiltered = request.EventData.TryGetValue("Usage.Is_Content_Filtered", out var contentFilteredStr)
                            && bool.TryParse(contentFilteredStr, out var contentFilteredValue) && contentFilteredValue;
                        var cachedTokens = request.EventData.TryGetValue("Usage.Cached_Tokens", out var cachedTokensStr)
                            && int.TryParse(cachedTokensStr, out var cachedTokensValue) ? cachedTokensValue : 0;
                        var inputTokens = request.EventData.TryGetValue("Usage.Prompt_Tokens", out var inputTokensStr)
                            && int.TryParse(inputTokensStr, out var inputTokensValue) ? inputTokensValue : 0;
                        var outputTokens = request.EventData.TryGetValue("Usage.Completion_Tokens", out var outputTokensStr)
                            && int.TryParse(outputTokensStr, out var outputTokensValue) ? outputTokensValue : 0;

                        _wrkCntxt.TokenomicsHandler.TokenMetricsCache.AddMetric(
                            request.UserID,
                            request.Model,
                            inputTokens,
                            outputTokens,
                            cachedTokens,
                            isJailbreakDetected,
                            isContentFiltered,
                            statusCode: (int)proxyResponse.StatusCode,
                            latencyMs: (DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds);
                    }
                }
                
                await _lifecycleManager.HandleBackgroundRequestLifecycle(request, processor!).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Background lifecycle management failed for request {Guid}", request.Guid);
            }
            finally
            {
                memoryBuffer?.Dispose();
                (processor as IDisposable)?.Dispose();
            }
        }
    }

    private async Task HandleBackgroundCheckResultAsync(
        RequestData request,
        HttpResponseMessage proxyResponse,
        IStreamProcessor processor,
        MemoryStream memoryBuffer)
    {

        if (!processor.BackgroundCompleted && !request.Debug)
        {
            _logger.LogDebug("Background check in progress - discarding {Bytes} bytes for request {Guid}",
                memoryBuffer.Length, request.Guid);
            return;
        }

        _logger.LogDebug("Background check completed or Debug mode - writing headers and {Bytes} bytes to blob for request {Guid}",
            memoryBuffer.Length, request.Guid);

        var pr = new ProxyData();
        ProxyHelperUtils.CopyResponseHeaders(proxyResponse, pr, s_stripResponseHeaders);
        if (pr.Headers != null && request.asyncWorker != null)
        {
            await request.asyncWorker.SaveResponseHeadersAsync(proxyResponse.StatusCode!, pr.Headers);
        }

        if (request.asyncWorker != null)
        {
            var outputStream = await request.asyncWorker.GetResponseDataStreamAsync();
            memoryBuffer.Position = 0;
            await memoryBuffer.CopyToAsync(outputStream).ConfigureAwait(false);
            await outputStream.FlushAsync().ConfigureAwait(false);
        }
    }


    // cts is returned to the caller who disposes of it
    private async Task<(CancellationTokenSource, double)> SetupAsyncWorkerAndTimeout(RequestData request)
    {
        double timeout = request.Timeout;
        CancellationTokenSource cts;

        // ✅ Dispose old CTS before creating new one and clear the reference
        _asyncExpelSource?.Dispose();
        _asyncExpelSource = null;
        
        if (request.runAsync)
        {
            timeout = _options.AsyncTimeout;
            if (request.asyncWorker is null)
            {
                var timeLeft = _options.AsyncTriggerTimeout - (int)(DateTime.UtcNow - request.EnqueueTime).TotalMilliseconds;
                timeLeft = Math.Max(1, timeLeft);
                request.asyncWorker = new AsyncWorker(request, timeLeft, _wrkCntxt.AsyncWorkerContext!);
                _ = request.asyncWorker.StartAsync();
            }

            _asyncExpelSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
            cts = _asyncExpelSource;
        }
        else
        {
            _asyncExpelSource = null;
            cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
        }

        return (cts, timeout);
    }


    // Exclude hop-by-hop and restricted headers that HttpListener manages
    private static readonly FrozenSet<string> s_excludedHeaders = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "Content-Length", "Transfer-Encoding", "Connection", "Proxy-Connection",
        "Keep-Alive", "Upgrade", "Trailer", "TE", "Date", "Server"
    );

}