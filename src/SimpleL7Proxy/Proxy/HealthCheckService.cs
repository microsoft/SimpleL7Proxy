using System.Text;
using System.Net;
using System.Collections.Concurrent;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Proxy;

/// <summary>
/// Optimized health check service that handles probe endpoints (/health, /readiness, /startup, /liveness).
/// Called multiple times per second, so performance is critical.
/// </summary>
public class HealthCheckService
{
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IUserPriorityService? _userPriority;
    private readonly IEventClient? _eventClient;
    private readonly Func<string> _getWorkerState;
    
    // Cache for health check responses to reduce allocations
    private readonly StringBuilder _stringBuilder;

    // Worker state tracking
    private static int _activeWorkers = 0;
    private static bool _readyToWork = false;
    private static int[] _states = [0, 0, 0, 0, 0, 0, 0, 0];

    public static int ActiveWorkers => _activeWorkers;
    public static bool IsReadyToWork => _readyToWork;
    
    public HealthCheckService(
        IBackendService backends,
        BackendOptions options,
        IConcurrentPriQueue<RequestData>? requestsQueue,
        IUserPriorityService? userPriority,
        IEventClient? eventClient)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestsQueue = requestsQueue;
        _userPriority = userPriority;
        _eventClient = eventClient;
        _getWorkerState = GetWorkerState;
        
        // Pre-allocate StringBuilder to reduce allocations
        _stringBuilder = new StringBuilder(512);
    }

    /// <summary>
    /// Get the current worker state string for monitoring/debugging
    /// </summary>
    public static string GetWorkerState()
    {
        return $"Count: {_activeWorkers} States: [ deq-{_states[0]} pre-{_states[1]} prxy-{_states[2]} -[snd-{_states[3]} rcv-{_states[4]}]-  wr-{_states[5]} rpt-{_states[6]} cln-{_states[7]} ]";
    }

    /// <summary>
    /// Increment a specific worker state counter (thread-safe)
    /// </summary>
    public static void IncrementState(int stateIndex)
    {
        if (stateIndex >= 0 && stateIndex < _states.Length)
        {
            Interlocked.Increment(ref _states[stateIndex]);
        }
    }

    /// <summary>
    /// Decrement a specific worker state counter (thread-safe)
    /// </summary>
    public static void DecrementState(int stateIndex)
    {
        if (stateIndex >= 0 && stateIndex < _states.Length)
        {
            Interlocked.Decrement(ref _states[stateIndex]);
        }
    }

    /// <summary>
    /// Increment the active worker count and set ready status if all workers are active
    /// </summary>
    public static void IncrementActiveWorkers(int totalWorkers)
    {
        int count = Interlocked.Increment(ref _activeWorkers);
        if (totalWorkers == count)
        {
            _readyToWork = true;
        }
    }

    /// <summary>
    /// Decrement the active worker count
    /// </summary>
    public static void DecrementActiveWorkers()
    {
        Interlocked.Decrement(ref _activeWorkers);
    }

    /// <summary>
    /// Processes a health check probe request and returns the appropriate status and message.
    /// </summary>
    /// <param name="path">The probe endpoint path (/health, /readiness, /startup, /liveness, /shutdown)</param>
    /// <param name="probeStatus">Output: HTTP status code for the probe</param>
    /// <param name="probeMessage">Output: Response message for the probe</param>
    public void GetProbeResponse(string path, out int probeStatus, out string probeMessage)
    {
        probeStatus = 200;
        probeMessage = "OK\n";

        // Cache these to avoid repeatedly calling the same methods
        int hostCount = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.CheckFailedStatus();

        switch (path)
        {
            case Constants.Health:
                BuildHealthResponse(hostCount, hasFailedHosts, out probeStatus, out probeMessage);
                break;

            case Constants.Readiness:
            case Constants.Startup:
                BuildReadinessResponse(hostCount, out probeStatus, out probeMessage);
                break;

            case Constants.Liveness:
                BuildLivenessResponse(hostCount, hasFailedHosts, out probeStatus, out probeMessage);
                break;

            case Constants.Shutdown:
                // Shutdown is a signal to unwedge workers and shut down gracefully
                break;
        }
    }

    /// <summary>
    /// Handles a probe request by processing it and writing the response to the HTTP context.
    /// This method encapsulates the complete probe handling logic.
    /// </summary>
    /// <param name="path">The probe endpoint path</param>
    /// <param name="context">The HTTP listener context to write the response to</param>
    /// <param name="eventData">Event data for logging probe details</param>
    /// <returns>A task representing the asynchronous operation</returns>
    public async Task HandleProbeRequestAsync(string path, HttpListenerContext context, ProxyEvent eventData)
    {
        GetProbeResponse(path, out int probeStatus, out string probeMessage);

        context.Response.StatusCode = probeStatus;
        context.Response.ContentType = "text/plain";
        context.Response.Headers.Add("Cache-Control", "no-cache");
        context.Response.KeepAlive = false;

        var healthMessage = Encoding.UTF8.GetBytes(probeMessage);
        context.Response.ContentLength64 = healthMessage.Length;

        await context.Response.OutputStream.WriteAsync(
            healthMessage,
            0,
            healthMessage.Length).ConfigureAwait(false);

        // Update event data for logging
        eventData["Probe"] = path;
        eventData["ProbeStatus"] = probeStatus.ToString();
        eventData["ProbeMessage"] = probeMessage;
        eventData.Type = EventType.Probe;
    }

    private void BuildHealthResponse(int hostCount, bool hasFailedHosts, out int probeStatus, out string probeMessage)
    {
        if (hostCount == 0 || hasFailedHosts)
        {
            probeStatus = 503;
            probeMessage = $"Not Healthy.  Active Hosts: {hostCount} Failed Hosts: {hasFailedHosts}\n";
        }
        else
        {
            // Use pre-allocated StringBuilder to reduce allocations
            lock (_stringBuilder)
            {
                _stringBuilder.Clear();
                _stringBuilder.Append("Replica: ")
                    .Append(_options.HostName)
                    .Append("".PadRight(30))
                    .Append(" SimpleL7Proxy: ")
                    .Append(Constants.VERSION)
                    .Append("\nBackend Hosts:\n  Active Hosts: ")
                    .Append(hostCount)
                    .Append("  -  ")
                    .Append(hasFailedHosts ? "FAILED HOSTS" : "All Hosts Operational")
                    .Append('\n');

                var hosts = _backends.GetHosts();
                if (hosts.Count > 0)
                {
                    foreach (var host in hosts)
                    {
                        _stringBuilder.Append(" Name: ")
                            .Append(host.Host)
                            .Append("  Status: ")
                            .Append(host.GetStatus(out int calls, out int errorCalls, out double average))
                            .Append('\n');
                    }
                }
                else
                {
                    _stringBuilder.Append("No Hosts\n");
                }

                // Add worker statistics
                _stringBuilder.Append("Worker Statistics:\n ")
                    .Append(_getWorkerState())
                    .Append('\n');

                // Add user priority queue state
                _stringBuilder.Append("User Priority Queue: ")
                    .Append(_userPriority?.GetState() ?? "N/A")
                    .Append('\n');

                // Add request queue count
                _stringBuilder.Append("Request Queue: ")
                    .Append(_requestsQueue?.thrdSafeCount.ToString() ?? "N/A")
                    .Append('\n');

                // Add event hub status
                _stringBuilder.Append("Event Hub: ");
                if (_eventClient != null)
                {
                    _stringBuilder.Append("Enabled  -  ")
                        .Append(_eventClient.Count)
                        .Append(" Items");
                }
                else
                {
                    _stringBuilder.Append("Disabled");
                }
                _stringBuilder.Append('\n');

                probeMessage = _stringBuilder.ToString();
            }
            probeStatus = 200;
        }
    }

    private void BuildReadinessResponse(int hostCount, out int probeStatus, out string probeMessage)
    {
        if (!IsReadyToWork || hostCount == 0)
        {
            probeStatus = 503;
            probeMessage = $"Not Ready .. hostCount = {hostCount} readyToWork = {IsReadyToWork}";
        }
        else
        {
            probeStatus = 200;
            probeMessage = "OK\n";
        }
    }

    private void BuildLivenessResponse(int hostCount, bool hasFailedHosts, out int probeStatus, out string probeMessage)
    {
        if (hostCount == 0)
        {
            probeStatus = 503;
            probeMessage = $"Not Lively.  Active Hosts: {hostCount} Failed Hosts: {hasFailedHosts}";
        }
        else
        {
            probeStatus = 200;
            probeMessage = "OK\n";
        }
    }
}
