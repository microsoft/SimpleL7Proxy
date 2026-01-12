using System.Text;
using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.BackupAPI;
using SimpleL7Proxy.Proxy;

using Shared.HealthProbe;

namespace SimpleL7Proxy.Proxy;

/// <summary>
/// Optimized health check service that handles probe endpoints (/health, /readiness, /startup, /liveness).
/// Called multiple times per second, so performance is critical.
/// Also manages worker state tracking for monitoring and diagnostics.
/// </summary>
public class HealthCheckService
{
    private readonly IBackendService _backends;
    private readonly BackendOptions _options;
    private readonly IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IUserPriorityService? _userPriority;
    private readonly IEventClient? _eventClient;
    private readonly IBackupAPIService? _backupAPIService;
    private readonly IServiceBusRequestService? _serviceBusRequestService;
    private readonly IBlobWriter? _blobWriter;
    private readonly Func<string> _getWorkerState;
    
    // Cache for health check responses to reduce allocations
    private readonly StringBuilder _stringBuilder;

    // Worker state tracking - using individual fields for better clarity and performance
    private static int _activeWorkers = 0;
    private static bool _readyToWork = false;
    
    // Track current state per worker ID
    private static readonly ConcurrentDictionary<int, WorkerState?> _workerCurrentState = new();
    
    // State counters for each worker state (thread-safe via Interlocked operations)
    private static int _dequeueingCount = 0;
    private static int _preProcessingCount = 0;
    private static int _proxyingCount = 0;
    private static int _sendingCount = 0;
    private static int _receivingCount = 0;
    private static int _writingCount = 0;
    private static int _reportingCount = 0;
    private static int _cleanupCount = 0;

    public static int ActiveWorkers => _activeWorkers;
    public static bool IsReadyToWork => System.Threading.Volatile.Read(ref _readyToWork);
    
    public HealthCheckService(
        IBackendService backends,
        IOptions<BackendOptions> options,
        IConcurrentPriQueue<RequestData>? requestsQueue,
        IUserPriorityService? userPriority,
        IEventClient? eventClient,
        IServiceBusRequestService? serviceBusRequestService = null,
        IBlobWriter? blobWriter = null,
        IBackupAPIService? backupAPIService = null)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _requestsQueue = requestsQueue;
        _userPriority = userPriority;
        _eventClient = eventClient;
        _serviceBusRequestService = serviceBusRequestService;
        _blobWriter = blobWriter;
        _backupAPIService = backupAPIService;
        _getWorkerState = GetWorkerState;
        
        // Pre-allocate StringBuilder to reduce allocations
        _stringBuilder = new StringBuilder(512);
    }

    /// <summary>
    /// Get the current worker state string for monitoring/debugging
    /// </summary>
    public static string GetWorkerState()
    {
        return $"Count: {_activeWorkers} States: [ deq-{_dequeueingCount} pre-{_preProcessingCount} prxy-{_proxyingCount} -[snd-{_sendingCount} rcv-{_receivingCount}]-  wr-{_writingCount} rpt-{_reportingCount} cln-{_cleanupCount} ]";
    }

    /// <summary>
    /// Enter a worker state. Automatically exits the previous state if the worker was in one.
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker</param>
    /// <param name="newState">The state to enter</param>
    public static void EnterState(int workerId, WorkerState newState)
    {
        // Get and update the current state atomically
        var oldState = _workerCurrentState.AddOrUpdate(
            workerId,
            newState,
            (_, currentState) =>
            {
                // Exit the old state if one exists
                if (currentState.HasValue)
                {
                    ExitStateInternal(currentState.Value);
                }
                return newState;
            });
        
        // If this is the first time we're seeing this worker, oldState will be the newState we just set
        // Otherwise, oldState is the previous value and we've already exited it in the update function
        if (oldState.Equals(newState))
        {
            // First time setting state for this worker - just enter the new state
            EnterStateInternal(newState);
        }
        else
        {
            // We already exited the old state in the update function, just enter the new state
            EnterStateInternal(newState);
        }
    }

    /// <summary>
    /// Internal method to increment a state counter
    /// </summary>
    private static void EnterStateInternal(WorkerState state)
    {
        switch (state)
        {
            case WorkerState.Dequeuing:
                Interlocked.Increment(ref _dequeueingCount);
                break;
            case WorkerState.PreProcessing:
                Interlocked.Increment(ref _preProcessingCount);
                break;
            case WorkerState.Proxying:
                Interlocked.Increment(ref _proxyingCount);
                break;
            case WorkerState.Sending:
                Interlocked.Increment(ref _sendingCount);
                break;
            case WorkerState.Receiving:
                Interlocked.Increment(ref _receivingCount);
                break;
            case WorkerState.Writing:
                Interlocked.Increment(ref _writingCount);
                break;
            case WorkerState.Reporting:
                Interlocked.Increment(ref _reportingCount);
                break;
            case WorkerState.Cleanup:
                Interlocked.Increment(ref _cleanupCount);
                break;
        }
    }

    /// <summary>
    /// Exit a worker state (decrements the state counter in a thread-safe manner)
    /// </summary>
    /// <param name="state">The state to exit</param>
    private static void ExitStateInternal(WorkerState state)
    {
        switch (state)
        {
            case WorkerState.Dequeuing:
                Interlocked.Decrement(ref _dequeueingCount);
                break;
            case WorkerState.PreProcessing:
                Interlocked.Decrement(ref _preProcessingCount);
                break;
            case WorkerState.Proxying:
                Interlocked.Decrement(ref _proxyingCount);
                break;
            case WorkerState.Sending:
                Interlocked.Decrement(ref _sendingCount);
                break;
            case WorkerState.Receiving:
                Interlocked.Decrement(ref _receivingCount);
                break;
            case WorkerState.Writing:
                Interlocked.Decrement(ref _writingCount);
                break;
            case WorkerState.Reporting:
                Interlocked.Decrement(ref _reportingCount);
                break;
            case WorkerState.Cleanup:
                Interlocked.Decrement(ref _cleanupCount);
                break;
        }
    }

    /// <summary>
    /// Increment the active worker count and set ready status if all workers are active
    /// </summary>
    public static int IncrementActiveWorkers(int totalWorkers)
    {
        int count = Interlocked.Increment(ref _activeWorkers);
        if (totalWorkers == count)
        {
            System.Threading.Volatile.Write(ref _readyToWork, true);
        }
        return count;
    }

    /// <summary>
    /// Decrement the active worker count and clean up worker state tracking
    /// </summary>
    /// <param name="workerId">The unique identifier of the worker being shut down</param>
    public static void DecrementActiveWorkers(int workerId)
    {
        Interlocked.Decrement(ref _activeWorkers);
        
        // Exit the worker's current state if it has one
        if (_workerCurrentState.TryRemove(workerId, out var currentState) && currentState.HasValue)
        {
            ExitStateInternal(currentState.Value);
        }
    }

    public async Task HealthResponseAsync(HttpListenerContext lc)
    {
        int hostCount = _backends.ActiveHostCount();
        bool hasFailedHosts = _backends.CheckFailedStatus();
        BuildHealthResponse(hostCount, hasFailedHosts, out int probeStatus, out string probeMessage);

        try
        {
            lc.Response.ContentType = "text/plain";
            lc.Response.Headers["Cache-Control"] = "no-cache";
            lc.Response.Headers["Connection"] = "close";

            switch (probeStatus)
            {
                case 200:
                    lc.Response.StatusCode = (int)HttpStatusCode.OK;
                    break;
                default:
                    lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    break;
            }

            lc.Response.ContentLength64 = probeMessage.Length;
            await lc.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(probeMessage), 0, probeMessage.Length);
        }
        finally
        {
            try
            {
                lc.Response.Close();
            }
            catch { }
        }   
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

                // ThreadPool availability snapshot
                ThreadPool.GetAvailableThreads(out int workersAvailable, out int ioAvailable);
                ThreadPool.GetMinThreads(out int workersMin, out int ioMin);
                ThreadPool.GetMaxThreads(out int workersMax, out int ioMax);
                _stringBuilder.Append("ThreadPool:\n  Workers - Available/Min/Max: ")
                    .Append(workersAvailable)
                    .Append(" / ")
                    .Append(workersMin)
                    .Append(" / ")
                    .Append(workersMax)
                    .Append("\n  IOCP    - Available/Min/Max: ")
                    .Append(ioAvailable)
                    .Append(" / ")
                    .Append(ioMin)
                    .Append(" / ")
                    .Append(ioMax)
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

                // Add backup API service statistics
                if (_backupAPIService != null)
                {
                    var eventStats = _backupAPIService.GetEventStatistics();
                    var errorStats = _backupAPIService.GetErrorStatistics();
                    
                    var eventsLastMin = eventStats[0];
                    var eventsLast5Min = eventStats.Take(5).Sum(x => x.Value);
                    var eventsLast10Min = eventStats.Values.Sum();
                    
                    var errorsLastMin = errorStats[0];
                    var errorsLast5Min = errorStats.Take(5).Sum(x => x.Value);
                    var errorsLast10Min = errorStats.Values.Sum();
                    
                    var totalAttempts = eventsLast10Min + errorsLast10Min;
                    var errorRate = totalAttempts > 0 ? (double)errorsLast10Min / totalAttempts * 100 : 0;
                    
                    _stringBuilder.Append("Backup API Service:\n")
                        .Append("  Events (1/5/10 min): ")
                        .Append(eventsLastMin)
                        .Append(" / ")
                        .Append(eventsLast5Min)
                        .Append(" / ")
                        .Append(eventsLast10Min)
                        .Append("\n  Errors (1/5/10 min): ")
                        .Append(errorsLastMin)
                        .Append(" / ")
                        .Append(errorsLast5Min)
                        .Append(" / ")
                        .Append(errorsLast10Min)
                        .Append("\n  Error Rate (10min): ")
                        .Append(errorRate.ToString("F2"))
                        .Append("%\n");
                }
                else
                {
                    _stringBuilder.Append("Backup API Service: Disabled\n");
                }

                // Add Service Bus statistics
                if (_serviceBusRequestService != null)
                {
                    var sbStats = _serviceBusRequestService.GetStatistics();
                    _stringBuilder.Append("Service Bus:\n")
                        .Append("  Status: ")
                        .Append(sbStats.isEnabled ? "Enabled" : "Disabled")
                        .Append("\n  Connection: ")
                        .Append(sbStats.connectionInfo ?? "N/A")
                        .Append("\n  Total Messages: ")
                        .Append(sbStats.totalMessages)
                        .Append("\n  Total Batches: ")
                        .Append(sbStats.totalBatches)
                        .Append("\n  Queue Depth: ")
                        .Append(sbStats.queueDepth)
                        .Append('\n');
                }
                else
                {
                    _stringBuilder.Append("Service Bus: Not Configured\n");
                }

                // Add Blob Storage statistics
                if (_blobWriter != null)
                {
                    var blobInfo = _blobWriter.GetConnectionInfo();
                    _stringBuilder.Append("Blob Storage:\n")
                        .Append("  Connection: ")
                        .Append(blobInfo)
                        .Append("\n  Initialized: ")
                        .Append(_blobWriter.IsInitialized ? "Yes" : "No")
                        .Append("\n  Async Mode: ")
                        .Append(_options.AsyncModeEnabled ? "Enabled" : "Disabled")
                        .Append('\n');
                }
                else
                {
                    _stringBuilder.Append("Blob Storage: Not Configured\n");
                }

                probeMessage = _stringBuilder.ToString();
            }
            probeStatus = 200;
        }
    }

    public HealthStatusEnum GetReadinessStatus()
    {
        if (!IsReadyToWork)
        {
            return HealthStatusEnum.ReadinessZeroHosts;
        }

        int hostCount = _backends.ActiveHostCount();
        if (hostCount == 0)
        {
            return HealthStatusEnum.ReadinessZeroHosts;
        }

        if (_backends.CheckFailedStatus())
        {
            return HealthStatusEnum.ReadinessFailedHosts;
        }

        return HealthStatusEnum.ReadinessReady;
        
    }

    public HealthStatusEnum GetStartupStatus() {

        if (!IsReadyToWork)
        {
            return HealthStatusEnum.StartupZeroHosts;
        }

        int hostCount = _backends.ActiveHostCount();
        if (hostCount == 0)
        {
            return HealthStatusEnum.StartupZeroHosts;
        }

        if (_backends.CheckFailedStatus())
        {
            return HealthStatusEnum.StartupFailedHosts;
        }

        return HealthStatusEnum.StartupReady;   
    }
}
