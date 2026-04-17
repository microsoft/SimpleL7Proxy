using System.Net;
using System.Text.Json;
using System.Collections.Frozen;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Threading;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Async.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Async.ServiceBus;
using System.Text;

using Shared.HealthProbe;


namespace SimpleL7Proxy;
// This class represents a server that listens for HTTP requests and processes them.
// It uses a priority queue to manage incoming requests and supports telemetry for monitoring.
// If the incoming request has the S7PPriorityKey header, it will be assigned a priority based the S7PPriority header.
public class Server :  BackgroundService, IConfigChangeSubscriber
{
    //    private readonly IBackendOptions? _options;
    private readonly ProxyConfig _options;
    private readonly HttpListener _httpListener;

    private readonly IEndpointMonitorService _backends;

    private readonly IUserPriorityService _userPriority;
    private readonly IUserProfileService _userProfile;
    private CancellationTokenSource? _cancellationTokenSource;
    private CancellationTokenSource? _probesCts; // Controls when probe serving finally stops
    private readonly IConcurrentPriQueue<RequestData> _requestsQueue;// = new ConcurrentPriQueue<RequestData>();
    //private readonly IServiceBusRequestService _serviceBusRequestService;
    private readonly ILogger<Server> _logger;
    private readonly IBlobWriter _blobWriter;
    private static bool _isShuttingDown = false;
    private readonly string _priorityHeaderName;
    private readonly HealthCheckService _healthService;

    private readonly IEventClient? _eventHubClient;
    private static ProxyEvent _staticEvent = new ProxyEvent();
    private static ProxyEvent _probe = new ProxyEvent();
    private readonly ProbeServer _probeServer;

    // Precomputed frozen collections for O(1) hot-path lookups, recomputed on config change
    private volatile FrozenSet<string> _disallowedHeaders = null!;
    private volatile FrozenDictionary<string, int> _priorityKeyToValue = null!;

    // Precomputed validation rules to avoid dictionary iteration and string ops per request
    private readonly record struct ValidateHeaderRule(string SourceHeader, string AllowedValuesHeader, string DisplayName);
    private ValidateHeaderRule[] _validateHeaderRules = null!;

    // Constructor to initialize the server with backend options and telemetry client.
    public Server(
        IConcurrentPriQueue<RequestData> requestsQueue,
        IOptions<ProxyConfig> backendOptions,
        IHostApplicationLifetime appLifetime,
        IUserPriorityService userPriority,
        IUserProfileService userProfile,
        //IServiceBusRequestService serviceBusRequestService,
        IEventClient? eventHubClient,
        IEndpointMonitorService backends,
        IBlobWriter blobWriter,
        HealthCheckService healthService,
        ProbeServer probeServer,
        ConfigChangeNotifier configChangeNotifier,
        ILogger<Server> logger)
    {
        ArgumentNullException.ThrowIfNull(backendOptions, nameof(backendOptions));
        ArgumentNullException.ThrowIfNull(backends, nameof(backends));
        ArgumentNullException.ThrowIfNull(userPriority, nameof(userPriority));
        ArgumentNullException.ThrowIfNull(userProfile, nameof(userProfile));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));
        ArgumentNullException.ThrowIfNull(appLifetime, nameof(appLifetime));
        ArgumentNullException.ThrowIfNull(requestsQueue, nameof(requestsQueue));
        ArgumentNullException.ThrowIfNull(blobWriter, nameof(blobWriter));
        ArgumentNullException.ThrowIfNull(healthService, nameof(healthService));
        ArgumentNullException.ThrowIfNull(probeServer, nameof(probeServer));
        //ArgumentNullException.ThrowIfNull(serviceBusRequestService, nameof(serviceBusRequestService));

        _options = backendOptions.Value;
        _backends = backends;
        _eventHubClient = eventHubClient;
        _userPriority = userPriority;
        _userProfile = userProfile;
        _logger = logger;
        _blobWriter = blobWriter;
        _healthService = healthService;
        _requestsQueue = requestsQueue;
        _priorityHeaderName = _options.PriorityKeyHeader;
        _probeServer = probeServer;

        configChangeNotifier.Subscribe(this,
           [options => options.PriorityKeyHeader,
            options => options.PriorityKeys,
            options => options.PriorityValues,
            options => options.UserIDFieldName,
            options => options.UserProfileHeader,
            options => options.ValidateHeaders,
            options => options.Timeout,
            options => options.AsyncModeEnabled,
            options => options.DefaultPriority,
            options => options.ValidateAuthAppID,
            options => options.ValidateAuthAppIDHeader,
            options => options.DisallowedHeaders,
            options => options.RequiredHeaders,
            options => options.UniqueUserHeaders,
            options => options.AsyncClientRequestHeader,
            options => options.TimeoutHeader,
            options => options.DefaultTTLSecs,
            options => options.TTLHeader,
            options => options.MaxQueueLength,
            options => options.PollInterval

            // COLD OPTIONS (require restart, so not subscribed for live updates):
            // options => options.Port,  
            // options => options.UseProfiles,
            // options => options.IDStr,
            // options => options.CircuitBreakerTimeslice,   display only
            ]);

        InitVars();

        var _listeningUrl = $"http://+:{_options.Port}/";

        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add(_listeningUrl);

        // Initialize probe data pool to avoid allocations in hot path
        // _probeDataPool = new ProbeData[ProbePoolSize];
        // for (int i = 0; i < ProbePoolSize; i++)
        // {
        //     _probeDataPool[i] = new ProbeData();
        // }

        // Server config is logged at startup in ExecuteAsync alongside the listening message
    }

    public void InitVars()
    {
        // Recompute frozen sets from updated options
        _disallowedHeaders = _options.DisallowedHeaders.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _priorityKeyToValue = _options.PriorityKeys
            .Zip(_options.PriorityValues)
            .ToFrozenDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);

        _validateHeaderRules = _options.ValidateHeaders
            .Select(kvp => new ValidateHeaderRule(
                kvp.Key,
                kvp.Value,
                kvp.Key.StartsWith("S7", StringComparison.Ordinal) ? kvp.Key[2..] : kvp.Key))
            .ToArray();
    }

    public Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        ProxyConfig backendOptions,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[CONFIGS] Server changed — Settings live updated without restart");

        InitVars();

        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        _isShuttingDown = true;
    }

    public async Task StopListening(CancellationToken cancellationToken)
    {
        _isShuttingDown = true;
        _cancellationTokenSource?.Cancel();
        _logger.LogInformation("[SHUTDOWN] ⏹ Server stopped accepting new requests (probes still active)");
    }

    /// <summary>
    /// Stops the HttpListener entirely, ending probe serving.
    /// Call this as the very last step in shutdown so the container orchestrator
    /// continues to see healthy probes while other services drain.
    /// </summary>
    public async Task StopProbes(CancellationToken cancellationToken)
    {
        _probesCts?.Cancel();
        _logger.LogInformation("[SHUTDOWN] ⏹ Health probe serving stopped");

        // Wait for the Run() loop to actually exit
        if (ExecuteTask != null)
        {
            try { await ExecuteTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    // public ConcurrentPriQueue<RequestData> Queue() {
    //     return _requestsQueue;
    // }

    // Method to start the server and begin processing requests.
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        string serverInfo = $"Port: {_options.Port}, Timeout: {_options.Timeout}ms, Workers: {_options.Workers}, LoadBalanceMode: {_options.LoadBalanceMode}, ValidateAuthAppID: {_options.ValidateAuthAppID}, AsyncModeEnabled: {_options.AsyncModeEnabled}";
        try
        {
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _probesCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _httpListener.Start();

            _logger.LogInformation($"[HTTP(s)] ✓ Server listening: {serverInfo}");
            // Additional setup or async start operations can be performed here

            _requestsQueue.StartSignaler(cancellationToken);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "[HTTP(s)] ✗ HttpListener failed to start {info}, {Message}", serverInfo, ex.Message);
            throw new Exception($"Failed to start the server on port {_options.Port}.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HTTP(s)] ✗ Server failed to start — {info}, {Message}", serverInfo, ex.Message);
            throw new Exception("An error occurred while starting the server.", ex);
        }

        // Use _probesCts.Token so the Run loop continues serving probes even after
        // StopListening cancels _cancellationTokenSource (which sets _isShuttingDown=true).
        // Only StopProbes() cancels _probesCts, killing the loop entirely.
        var token = _probesCts.Token;

        await Run(token);
    }  

    bool initialStartup = true;
    // Continuously listens for incoming HTTP requests and processes them.
    // Requests are enqueued with a priority based on specific headers.
    // The method runs until a cancellation is requested.
    // Each request is enqueued with a priority into BlockingPriorityQueue.
    public async Task Run(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(_httpListener, nameof(_httpListener));
        //ArgumentNullException.ThrowIfNull(_cancellationTokenSource, nameof(_cancellationTokenSource));
        ArgumentNullException.ThrowIfNull(_options, nameof(_options));

        long counter = 0;
        int livenessPriority = _options.PriorityValues.Min();
        bool doUserProfile = _options.UseProfiles;
        // Only enable async mode if configured AND blob storage is available (not using NullBlobWriter)
        bool doAsync = _options.AsyncModeEnabled && !(_blobWriter is NullBlobWriter);
        int maxEvents = _options.MaxUndrainedEvents;
        int halfMaxEvents = maxEvents / 2;

        _probe.Type = EventType.Probe;

        // Hoist TCS + cancellation registration outside the loop — the TCS stays
        // incomplete until cancellation fires, so one instance serves all iterations.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ctr = cancellationToken.Register(() => tcs.TrySetResult());
        var ptimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        while (!cancellationToken.IsCancellationRequested)
        {
            ProxyEvent ed = null!;

            try
            {
                // Use the CancellationToken to asynchronously wait for an HTTP request.
                var getContextTask = _httpListener.GetContextAsync();

                var completedTask = await Task.WhenAny(getContextTask, tcs.Task).ConfigureAwait(false);

                if (completedTask == getContextTask)
                {
                    var lc = await getContextTask.ConfigureAwait(false);
                    if (lc == null || lc.Request == null)
                    {
                        continue;
                    }
                    var isprobe = false;
                    var probePath = lc.Request.Url?.PathAndQuery;

                    // Liveness/Readiness/Startup: respond immediately, don't enqueue
                    if (probePath is Constants.Liveness or Constants.Readiness or Constants.Startup)
                    {
                        var (probeType, code) = probePath switch
                        {
                            Constants.Liveness  => ("Liveness",  await _probeServer.LivenessResponseAsync(lc)),
                            Constants.Readiness => ("Readiness", await _probeServer.ReadinessResponseAsync(lc)),
                            _                   => ("Startup",   await _probeServer.StartupResponseAsync(lc)),
                        };
                        _probe.Uri = lc.Request.Url!;
                        _probe["ProbeType"] = probeType;
                        _probe["StatusCode"] = ((int)code).ToString();
                        _probe.Status = code; 
                        _probe.SendEvent();

                        if (initialStartup )
                        {
                            if (code != HttpStatusCode.OK)
                                _logger.LogInformation("[- PROBE] SERVICE NOT READY: {ProbeType} probe responded with {Code} during startup", probeType, code);
                            else
                                initialStartup = false;
                        }
                        // Console.WriteLine($"[PROBE] {probeType} probe received, responded with {code}");
                        continue;
                    }
                    // Console.WriteLine($"[NOT PROBE] {probePath} received, processing as normal request");

                    // Health/HealthDetail/ForceGC: mark as probe, enqueue for worker to handle and log
                    if (probePath is Constants.Health or Constants.HealthDetail or Constants.ForceGC)
                    {
                        isprobe = true;
                    }
                    
                    int priority = _options.DefaultPriority;
                    int userPriorityBoost = 0;
                    var notEnqued = false;
                    int notEnquedCode = 0;
                    var retrymsg = "";
                    var logmsg = "";

                    counter++;
                    var requestId = _options.IDStr + counter.ToString();

                    //delayCts.Cancel();
                    var rd = new RequestData(lc, requestId);
                    ed = rd.EventData;
                    ed["Date"] = DateTime.UtcNow.ToString("o");
                    ed.Uri = rd.Context!.Request.Url!;
                    ed.Method = rd.Method ?? "N/A";

                    ed["Path"] = rd.Path ?? "N/A";
                    ed["RequestHost"] = rd.Headers["Host"] ?? "N/A";
                    ed["RequestUserAgent"] = rd.Headers["User-Agent"] ?? "N/A";

                    // if it's a probe, then bypass all the below checks and enqueue the request 
                    if (isprobe)
                    {
                        // /startup runs a priority of 0,   otherwise run at highest priority ( lower is more urgent )
                        priority = 0;//(rd.Path == Constants.Liveness || rd.Path == Constants.Health) ? livenessPriority : 0;

                        // bypass all the below checks and enqueue the request
                        _requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime, true);
                        continue;
                    }

                    if (!_isShuttingDown)
                    {
                        int eventCount = _probeServer.EventCount;
                        if (eventCount > halfMaxEvents) {
                            int ticks = eventCount / 100;

                            // add a delay in case the number of events is high
                            for (int i = 0; i < ticks; i++)
                                await ptimer.WaitForNextTickAsync(cancellationToken);
                        }

                        if ( eventCount > maxEvents)
                        {
                            notEnqued = true;
                            notEnquedCode = 429;

                            retrymsg = ed["Message"] = "Max Events Exceeds Threshold";
                            logmsg = "MAX EVENTS  => 429:";
                        }
                        else if (await _backends.CheckFailedStatusAsync())
                        // Check circuit breaker status and enqueue the request
                        {
                            notEnqued = true;
                            notEnquedCode = 429;

                            ed["Message"] = "Circuit breaker on - 429";
                            retrymsg = $"Too many failures in last {_options.CircuitBreakerTimeslice} seconds";
                            logmsg = "Circuit breaker on  => 429:";
                        }
                        else if (_requestsQueue.thrdSafeCount >= _options.MaxQueueLength)
                        {
                            notEnqued = true;
                            notEnquedCode = 429;

                            retrymsg = ed["Message"] = "Queue is full";
                            logmsg = "Queue is full  => 429:";
                        }
                        else if (_backends.ActiveHostCount() == 0)
                        {
                            notEnqued = true;
                            notEnquedCode = 429;

                            retrymsg = ed["Message"] = "No active hosts";
                            logmsg = "No active hosts  => 429:";
                        }
                        else
                        {

                            try
                            {
                                rd.Debug = rd.Headers["S7PDEBUG"] != null && string.Equals(rd.Headers["S7PDEBUG"], "true", StringComparison.OrdinalIgnoreCase);


                                if (_options.ValidateAuthAppID)
                                {
                                    string? authAppID = rd.Headers[_options.ValidateAuthAppIDHeader];
                                    if (!string.IsNullOrEmpty(authAppID) && _userProfile.IsAuthAppIDValid(authAppID))
                                    {
                                        if (rd.Debug)
                                            _logger.LogInformation("AuthAppID {AuthAppID} is valid.", rd.Headers[_options.ValidateAuthAppIDHeader]);
                                    }
                                    else
                                    {
                                        if (rd.Debug)
                                            _logger.LogInformation("AuthAppID {AuthAppID} is invalid.", rd.Headers[_options.ValidateAuthAppIDHeader]);

                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.DisallowedAppID,
                                            HttpStatusCode.Forbidden,
                                            "Invalid AuthAppID: " + rd.Headers[_options.ValidateAuthAppIDHeader] + "\n"
                                        );
                                    }
                                }

                                // Remove any disallowed headers (FrozenSet for O(1) contains, but iterate to remove)
                                foreach (var header in _disallowedHeaders)
                                {
                                    if (rd.Debug && !string.IsNullOrEmpty(rd.Headers.Get(header)))
                                        _logger.LogInformation("Disallowed header {Header} removed from request.", header);
                                    rd.Headers.Remove(header);
                                }

                                rd.UserID = "";
                                // Normalize path once: ensure non-empty and starts with '/'
                                if (string.IsNullOrEmpty(rd.Path))
                                    rd.Path = "/";
                                else if (!rd.Path.StartsWith('/'))
                                    rd.Path = "/" + rd.Path;
                                rd.Headers["S7Path"] = rd.Path; // Copy path
                                // Lookup the user profile and add the headers to the request
                                if (doUserProfile)
                                {
                                    var requestUser = rd.Headers[_options.UserProfileHeader];
                                    if (!string.IsNullOrEmpty(requestUser))
                                    {
                                        rd.profileUserId = requestUser;
                                        (var headers, var isSoftDeleted, var isStale) = _userProfile.GetUserProfile(requestUser);

                                        if (headers != null && headers.Count > 0)
                                        {
                                            foreach (var header in headers)
                                            {
                                                if (!header.Key.StartsWith("internal-"))
                                                {
                                                    rd.Headers.Set(header.Key, header.Value);
                                                    if (rd.Debug)
                                                        _logger.LogInformation("Add Header: {Header} = {Value}", header.Key, header.Value);
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (rd.Debug)
                                                _logger.LogInformation("User profile for {User} not found.", requestUser);
                                            throw new ProxyErrorException(
                                                ProxyErrorException.ErrorType.UnknownProfile,
                                                HttpStatusCode.Forbidden,
                                                "User profile not found: " + requestUser + "\n"
                                            );
                                        }
                                    } 
                                    else if ( _options.UserConfigRequired)
                                    {
                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.UnknownProfile,
                                            HttpStatusCode.Forbidden,
                                            "User profile not found: " + requestUser + "\n"
                                        );
                                    }
                                }

                                // Check for any required headers
                                if (_options.RequiredHeaders.Count > 0)
                                {
                                    // Note: Returns the first missing required header only
                                    var missing = _options.RequiredHeaders.FirstOrDefault(x => string.IsNullOrEmpty(rd.Headers[x]));
                                    if (!string.IsNullOrEmpty(missing))
                                    {
                                        if (rd.Debug)
                                            _logger.LogInformation("Required header {Header} is missing from request.", missing);

                                        throw new ProxyErrorException(
                                            ProxyErrorException.ErrorType.IncompleteHeaders,
                                            HttpStatusCode.ExpectationFailed,
                                            "Required header is missing: " + missing
                                        );
                                    }
                                }

                                // Validate headers using precomputed rules and zero-alloc span tokenization
                                if (_validateHeaderRules.Length > 0)
                                {
                                    foreach (ref readonly var rule in _validateHeaderRules.AsSpan())
                                    {
                                        var lookup = rd.Headers[rule.SourceHeader]!.AsSpan().Trim();
                                        var allowedSpan = rd.Headers[rule.AllowedValuesHeader]!.AsSpan();
                                        bool matched = false;

                                        foreach (var range in allowedSpan.Split(','))
                                        {
                                            var pattern = allowedSpan[range].Trim();
                                            if (pattern.Length > 0 && pattern[^1] == '*')
                                            {
                                                if (lookup.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
                                                {
                                                    matched = true;
                                                    break;
                                                }
                                            }
                                            else if (lookup.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                                            {
                                                matched = true;
                                                break;
                                            }
                                        }

                                        if (!matched)
                                        {
                                            if (rd.Debug)
                                                _logger.LogInformation("Validation check failed for {DisplayName}: {Lookup}", rule.DisplayName, lookup.ToString());
                                            throw new ProxyErrorException(
                                                ProxyErrorException.ErrorType.InvalidHeader,
                                                HttpStatusCode.ExpectationFailed,
                                                $"Validation check failed for {rule.DisplayName}: {lookup}\n"
                                            );
                                        }
                                    }
                                    if (rd.Debug)
                                        _logger.LogInformation("Validation check passed for all headers.");
                                }

                                // Determine priority boost based on the UserID 
                                if (_options.UniqueUserHeaders.Count > 0)
                                {
                                    foreach (var header in _options.UniqueUserHeaders)
                                    {
                                        rd.UserID += rd.Headers[header] ?? "";
                                    }
                                }

                                if (String.IsNullOrEmpty(rd.UserID))
                                {
                                    rd.UserID = "defaultUser";
                                }

                                ed["UserID"] = rd.UserID;
                                ed["S7P-ID"] = rd.MID;

                                if (rd.Debug)
                                    _logger.LogInformation("UserID: {UserID}", rd.UserID);

                                // ASYNC: Determine if the request is allowed async operation
                                if (doAsync && bool.TryParse(rd.Headers[_options.AsyncClientRequestHeader], out var asyncEnabled) && asyncEnabled)
                                {
                                    var clientInfo = _userProfile.GetAsyncParams(rd.profileUserId);
                                    if (clientInfo != null)
                                    {
                                        rd.runAsync = true;
                                        rd.AsyncBlobAccessTimeoutSecs = clientInfo.AsyncBlobAccessTimeoutSecs;
                                        rd.BlobContainerName = clientInfo.ContainerName;
                                        rd.SBTopicName = clientInfo.SBTopicName;
                                        rd.AsyncClientConfig = clientInfo; // Store the full config for AsyncWorker
                                        ed["AsyncBlobContainer"] = clientInfo.ContainerName;
                                        ed["AsyncSBTopic"] = clientInfo.SBTopicName;
                                        ed["BlobAccessTimeout"] = clientInfo.AsyncBlobAccessTimeoutSecs.ToString();
                                        ed["GenerateSAS"] = clientInfo.GenerateSasTokens.ToString();
                                    }

                                    if (rd.Debug)
                                    {
                                        _logger.LogInformation("AsyncEnabled: {AsyncEnabled}", rd.runAsync);
                                    }
                                }

                                // Determine priority boost based on the UserID
                                rd.Guid = _userPriority.addRequest(rd.UserID);
                                bool shouldBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue);
                                userPriorityBoost = shouldBoost ? 1 : 0;

                                ed["GUID"] = rd.Guid.ToString();

                                var priorityKey = rd.Headers[_priorityHeaderName];
                                // if (!string.IsNullOrEmpty(priorityKey) && _priorityKeys.Contains(priorityKey)) //lookup the priority
                                // {
                                //     var index = _options.PriorityKeys.IndexOf(priorityKey);
                                //     if (index >= 0)
                                //     {
                                //         priority = _options.PriorityValues[index];
                                //     }
                                // }
                                if (!string.IsNullOrEmpty(priorityKey) && _priorityKeyToValue.TryGetValue(priorityKey, out var mappedPriority))
                                {
                                    priority = mappedPriority;
                                }
                                rd.Priority = priority;
                                rd.Priority2 = userPriorityBoost;
                                rd.EnqueueTime = DateTime.UtcNow;

                                ed["S7P-Priority"] = priority.ToString();
                                ed["S7P-Priority2"] = userPriorityBoost.ToString();

                                // Save the timeout header value if it exists
                                if (rd.Headers[_options.TimeoutHeader] != null && int.TryParse(rd.Headers[_options.TimeoutHeader], out var timeout))
                                {
                                    rd.defaultTimeout = timeout;
                                }
                                else
                                {
                                    rd.defaultTimeout = _options.Timeout;
                                }

                                // Calculate expiresAt time based on the timeout header or default TTL
                                rd.CalculateExpiration(_options.DefaultTTLSecs, _options.TTLHeader);
                                ed["DefaultTimeout"] = rd.defaultTimeout.ToString();

                                // Enqueue the request
                                if (!_requestsQueue.Enqueue(rd, priority, userPriorityBoost, rd.EnqueueTime))
                                {
                                    notEnqued = true;
                                    notEnquedCode = 429;

                                    retrymsg = ed["Message"] = "Failed to enqueue request";
                                    logmsg = "Failed to enqueue request  => 429:";
                                }

                                // ASYNC: If the request is allowed to run async, set the status
                                if (!notEnqued && doAsync)
                                {
                                    rd.SBStatus = ServiceBusMessageStatusEnum.Queued;
                                }

                            }
                            catch (ProxyErrorException e)
                            {
                                notEnqued = true;
                                notEnquedCode = (int)e.StatusCode;

                                logmsg = retrymsg = ed["Message"] = e.Message;
                            }
                        }   // end of allowed to proccess check
                    }
                    else
                    {
                        if (rd.Context is not null)
                        {
                            notEnqued = true;
                            notEnquedCode = 503;

                            retrymsg = ed["Message"] = "Server is shutting down.";
                            logmsg = "Connection rejected, Server is shutting down";
                            rd.Context.Response.Headers["Retry-After"] = "120"; // Retry after 120 seconds (adjust as needed)
                        }
                    }

                    // Distributed tracking:
                    // If incoming request already has a ParentId, use it otherwise use the current request's MID as ParentId
                    if (rd.Context?.Request.Headers["ParentId"] is string parentId && !string.IsNullOrEmpty(parentId))
                    {
                        rd.ParentId = parentId;
                    }
                    else
                    {
                        rd.ParentId = rd.MID;
                    }

                    ed.MID = rd.MID;
                    ed.ParentId = rd.ParentId;
                    ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();
                    ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                    ed["ExpiresAt"] = rd.ExpiresAtString;
                    ed["Priority"] = priority.ToString();
                    ed["Priority2"] = userPriorityBoost.ToString();

                    if (notEnqued)
                    {

                        if (rd.Context is not null)
                        {
                            ed.Type = EventType.ServerError;
                            ed["ErrorDetail"] = "EnqueueFailed";
                            ed.Status = (HttpStatusCode)notEnquedCode;
                            ed["QueueLength"] = _requestsQueue.thrdSafeCount.ToString();
                            ed["ActiveHosts"] = _backends.ActiveHostCount().ToString();

                            if (!_isShuttingDown)
                                _logger.LogError($"{logmsg}: Queue Length: {_requestsQueue.thrdSafeCount}, Active Hosts: {_backends.ActiveHostCount()}");

                            try
                            {
                                rd.Context.Response.StatusCode = notEnquedCode;
                                ed["Retry-After"] = rd.Context.Response.Headers["Retry-After"] = (_backends.ActiveHostCount() == 0) ? _options.PollInterval.ToString() : "500";

                                using (var writer = new System.IO.StreamWriter(rd.Context.Response.OutputStream))
                                {
                                    await writer.WriteAsync(retrymsg).ConfigureAwait(false);
                                }
                                rd.Context.Response.Close();
                            }
                            catch (Exception ex)
                            {
                                var msg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;

                                if (!msg.Contains("Broken pipe") && !msg.Contains("Unable to write data to the transport connection"))
                                {
                                    _logger.LogError($"Request was not enqueue'd and got an error writing on network: {msg}");                               
                                }
                                    
                                ed["ErrorWritingResponse"] = msg;
                            }

                            // if ( !_isShuttingDown)
                            //     _staticEvent.WriteOutput($"Pri: {priority} Stat: {notEnquedCode} Path: {rd.Path}");
                        }

                        ed.SendEvent();
                        _userPriority.removeRequest(rd.UserID, rd.Guid);
                    }
                    else
                    {
                        ProxyEvent temp_ed = new(ed);
                        temp_ed.Type = EventType.ProxyRequestEnqueued;
                        temp_ed["Message"] = "Enqueued request";

                        temp_ed.SendEvent();
                        _logger.LogDebug("[Queue:Enqueue:{Guid}] Request queued - Priority: {Priority}, User: {UserId}, Async: {IsAsync}, QueueLen: {QueueLength}, ActiveHosts: {ActiveHosts}",
                            rd.Guid, priority, rd.UserID, rd.runAsync, _requestsQueue.thrdSafeCount, _backends.ActiveHostCount());
                    }
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested(); // This will throw if the token is cancelled while waiting for a request.
                }
                //}
            }
            catch (IOException ioEx)
            {
                ed.WriteOutput($"An IO exception occurred: {ioEx.Message}");
            }
            catch (OperationCanceledException)
            {
                break; // Exit the loop
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred");
                _staticEvent.WriteOutput($"Error: {e.Message}\n{e.StackTrace}");
            }
        }

        _staticEvent.WriteOutput("[SHUTDOWN] ✓ HTTP server stopped");
    }
}