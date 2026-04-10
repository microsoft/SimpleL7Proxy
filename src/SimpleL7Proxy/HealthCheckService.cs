using Microsoft.Extensions.Logging;
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

namespace SimpleL7Proxy;

/// <summary>
/// Optimized health check service that handles probe endpoints (/health, /readiness, /startup, /liveness).
/// Called multiple times per second, so performance is critical.
/// Also manages worker state tracking for monitoring and diagnostics.
/// </summary>
public class HealthCheckService
{
    private readonly IEndpointMonitorService _backends;
    private static ProxyConfig _options=null!;
    private readonly IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IUserPriorityService? _userPriority;
    private readonly IEventClient? _eventClient;
    private readonly IBackupAPIService? _backupAPIService;
    private readonly IServiceBusRequestService? _serviceBusRequestService;
    private readonly IBlobWriter? _blobWriter;
    private readonly IUserProfileService? _userProfileService;
    private readonly Func<string> _getWorkerState;
    private readonly ILogger<HealthCheckService> _logger;

    /// <summary>
    /// Task that completes when backend startup (token acquisition, health polling) succeeds.
    /// Callers can await this to block until backends are ready.
    /// Set by <see cref="BeginStartupMonitoring"/>.
    /// </summary>
    public Task BackendStartupTask { get; private set; } = Task.CompletedTask;

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

    private int _lastGen2Count = 0;
    private DateTime _lastFinalizerDrain = DateTime.UtcNow;
    private static TimeSpan s_finalizerDrainInterval;


    public HealthCheckService(
        IEndpointMonitorService backends,
        IOptions<ProxyConfig> options,
        IConcurrentPriQueue<RequestData>? requestsQueue,
        IUserPriorityService? userPriority,
        IEventClient? eventClient,
        ILogger<HealthCheckService> logger,

        IServiceBusRequestService? serviceBusRequestService = null,
        IBlobWriter? blobWriter = null,
        IBackupAPIService? backupAPIService = null,
        IUserProfileService? userProfileService = null)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _requestsQueue = requestsQueue;
        _userPriority = userPriority;
        _userProfileService = userProfileService;
        _eventClient = eventClient;
        _serviceBusRequestService = serviceBusRequestService;
        _blobWriter = blobWriter;
        _backupAPIService = backupAPIService;
        _getWorkerState = GetWorkerState;
        _logger = logger;

        // Pre-allocate StringBuilder to reduce allocations
        _stringBuilder = new StringBuilder(512);
        s_finalizerDrainInterval = _options.GC2InternalSecs > 0 ? TimeSpan.FromSeconds(_options.GC2InternalSecs) : TimeSpan.FromMinutes(15);
    }

    bool firstHealthCheck = true;
    bool backendsStarted = false;
    /// <summary>
    /// Starts backend health polling and token acquisition.
    /// Call once after backends have been registered (HostConfig.Initialize + RegisterBackends).
    /// </summary>
    public async Task BeginStartupMonitoring()
    {
        try
        {
            await _backends.WaitForStartupAsync().ConfigureAwait(false);
            backendsStarted = true;
             _logger.LogInformation("[STARTUP] ✓ Backend startup completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[STARTUP] ✗ Backend startup failed — {Message}", ex.Message);
            throw;
        }
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
        if ( !_options.TrackWorkers) return;

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

    // public async Task HealthResponseAsync(HttpListenerContext lc)
    // {
    //     int hostCount = _backends.ActiveHostCount();
    //     bool hasFailedHosts = _backends.CheckFailedStatus();
    //     BuildHealthResponse(hostCount, hasFailedHosts, out int probeStatus, out string probeMessage);

    //     try
    //     {
    //         lc.Response.ContentType = "text/plain";
    //         lc.Response.Headers["Cache-Control"] = "no-cache";
    //         lc.Response.Headers["Connection"] = "close";

    //         switch (probeStatus)
    //         {
    //             case 200:
    //                 lc.Response.StatusCode = (int)HttpStatusCode.OK;
    //                 break;
    //             default:
    //                 lc.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
    //                 break;
    //         }

    //         lc.Response.ContentLength64 = probeMessage.Length;
    //         await lc.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(probeMessage), 0, probeMessage.Length);
    //     }
    //     finally
    //     {
    //         try
    //         {
    //             lc.Response.Close();
    //         }
    //         catch { }
    //     }   
    // }

    public void BuildHealthResponse(string path, int hostCount, bool hasFailedHosts, DateTime requestTimestamp, out int probeStatus, out string probeMessage)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gcRemaining = s_finalizerDrainInterval - (DateTime.UtcNow - _lastFinalizerDrain);
        var now = DateTime.UtcNow;
        var elapsedMs = (now - requestTimestamp).TotalMilliseconds;
        var shared = new StringBuilder()
            .Append("Replica: ").Append(_options.HostName)
            .Append("  v").Append(Constants.VERSION)
            .Append("  Elapsed: ").Append(elapsedMs.ToString("F1")).Append(" ms").Append('\n')
            .Append("  Hosts: ").Append(hostCount)
            .Append(hasFailedHosts ? " [FAILED]" : " [OK]")
            .Append("  NextGC: ").Append(gcRemaining > TimeSpan.Zero ? gcRemaining.TotalSeconds.ToString("F0") + "s" : "ready");

        switch (path)
        {
            case Constants.ForceGC:
                {
                    probeStatus = 200;
                    lock (_stringBuilder)
                    {
                        _stringBuilder.Clear();
                        _stringBuilder.Append("Garbage Collection Forced\n").Append(shared).Append('\n');


                        var gcMemInfo = GC.GetGCMemoryInfo();
                        _stringBuilder.Append("\nMemory Statistics before calling GC:\n")
                            .Append("  Total Managed Memory: ")
                            .Append((GC.GetTotalMemory(false) / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n  Working Set: ")
                            .Append((process.WorkingSet64 / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n  Private Memory: ")
                            .Append((process.PrivateMemorySize64 / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n  Heap Size: ")
                            .Append((gcMemInfo.HeapSizeBytes / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n  Fragmented: ")
                            .Append((gcMemInfo.FragmentedBytes / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n  Gen0 Collections: ")
                            .Append(GC.CollectionCount(0))
                            .Append("\n  Gen1 Collections: ")
                            .Append(GC.CollectionCount(1))
                            .Append("\n  Gen2 Collections: ")
                            .Append(GC.CollectionCount(2))
                            .Append("\n  High Memory Load: ")
                            .Append((gcMemInfo.MemoryLoadBytes / 1024.0 / 1024.0).ToString("F2"))
                            .Append(" MB\n");

                        probeMessage = _stringBuilder.ToString();
                    }

                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                }
                break;

            case Constants.Health:
                {
                    // Use pre-allocated StringBuilder to reduce allocations
                    lock (_stringBuilder)
                    {
                        _stringBuilder.Clear();

                        _stringBuilder
                            .Append("═════════════════ SimpleL7Proxy Health Status ══════════════════\n")
                            .Append(' ').Append(shared).Append('\n');

                        // Probes
                        var (startupStatus, readinessStatus, undrainedEvents) = GetStatus();
                        _stringBuilder
                            .Append('\n')
                            .Append("─── Probes ────────────────────────────────────────────────────\n")
                            .Append(" /startup   : ").Append(startupStatus == HealthStatusEnum.StartupReady ? "200 OK" : "503 " + startupStatus).Append('\n')
                            .Append(" /readiness : ").Append(readinessStatus == HealthStatusEnum.ReadinessReady ? "200 OK" : "503 " + readinessStatus).Append('\n')
                            .Append(" Undrained  : ").Append(undrainedEvents).Append(" / ").Append(_options.MaxUndrainedEvents).Append('\n');

                        // Workers
                        _stringBuilder
                            .Append('\n')
                            .Append("─── Workers ───────────────────────────────────────────────────\n")
                            .Append(" Count  : ").Append(_activeWorkers).Append('\n')
                            .Append(" States : deq:").Append(_dequeueingCount)
                            .Append("  pre:").Append(_preProcessingCount)
                            .Append("  prxy:").Append(_proxyingCount)
                            .Append("  snd:").Append(_sendingCount)
                            .Append("  rcv:").Append(_receivingCount)
                            .Append("  wr:").Append(_writingCount)
                            .Append("  rpt:").Append(_reportingCount)
                            .Append("  cln:").Append(_cleanupCount)
                            .Append('\n');

                        // Queues
                        _stringBuilder
                            .Append('\n')
                            .Append("─── Queues ────────────────────────────────────────────────────\n")
                            .Append(" Priority Queue : ").Append(_userPriority?.GetState() ?? "N/A").Append('\n')
                            .Append(" Request Queue  : ").Append(_requestsQueue?.thrdSafeCount.ToString() ?? "N/A").Append('\n');

                        // Services
                        _stringBuilder
                            .Append('\n')
                            .Append("─── Services ──────────────────────────────────────────────────\n")
                            .Append(" Event Client    : ");
                        if (_eventClient != null)
                        {
                            _stringBuilder.Append(_eventClient.ClientType)
                                .Append(" (").Append(_eventClient.Count).Append(" items, ")
                                .Append(_eventClient.FlushedLastMinute).Append(" flushed/min)");
                        }
                        else
                        {
                            _stringBuilder.Append("Disabled");
                        }
                        _stringBuilder.Append('\n');
                        if (_options.AsyncModeEnabled)
                        {
                            _stringBuilder.Append(" Backup API      : ").Append(_backupAPIService != null ? "Enabled" : "Disabled").Append('\n')
                                .Append(" Service Bus     : ").Append(_serviceBusRequestService != null ? "Enabled" : "Disabled").Append('\n')
                                .Append(" Blob Storage    : ").Append(_blobWriter != null ? "Enabled" : "Disabled").Append('\n');
                        }
                        else
                        {
                            _stringBuilder.Append(" Async Services  : Off (Backup API, Service Bus, Blob Storage)\n");
                        }
                        _stringBuilder.Append(" ProxyEvent Pool : ");
                        if (_options.ReuseEvents)
                        {
                            _stringBuilder.Append(RequestData.EventDataPoolCheckedOut)
                                .Append(" / ").Append(RequestData.EventDataPoolMaxSize)
                                .Append(" checked out");
                        }
                        else
                        {
                            _stringBuilder.Append("Disabled");
                        }
                        _stringBuilder.Append('\n');

                        // Memory
                        {
                            var gcMemInfo = GC.GetGCMemoryInfo();
                            _stringBuilder
                                .Append('\n')
                                .Append("─── Memory ────────────────────────────────────────────────────\n")
                                .Append(" Managed    : ").Append((GC.GetTotalMemory(false) / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                                .Append(" Working Set: ").Append((process.WorkingSet64 / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                                .Append(" Private    : ").Append((process.PrivateMemorySize64 / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                                .Append(" Heap Size  : ").Append((gcMemInfo.HeapSizeBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                                .Append(" Fragmented : ").Append((gcMemInfo.FragmentedBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                                .Append(" GC (0/1/2) : ").Append(GC.CollectionCount(0))
                                .Append(" / ").Append(GC.CollectionCount(1))
                                .Append(" / ").Append(GC.CollectionCount(2)).Append('\n')
                                .Append(" Memory Load: ").Append((gcMemInfo.MemoryLoadBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n");
                        }

                        _stringBuilder.Append("═══════════════════════════════════════════════════════════════\n");

                        probeMessage = _stringBuilder.ToString();
                    }
                    probeStatus = 200;
                }
                break;

            default:
                probeStatus = 404;
                probeMessage = "Unknown endpoint\n";
                break;

            case Constants.HealthDetail:
                {
                    probeStatus = 200;
                    var sb = new StringBuilder(2048);
                    var gcMemInfo = GC.GetGCMemoryInfo();
                    var totalManaged = GC.GetTotalMemory(false);
                    var privateBytes = process.PrivateMemorySize64;
                    var workingSet = process.WorkingSet64;
                    var nativeEstimate = privateBytes - totalManaged;
                    var gen2Ready = GC.CollectionCount(2) > _lastGen2Count;

                    sb.Append("═══════════════════ Detailed Health Diagnostics ════════════════════\n")
                      .Append(shared)
                      .Append(" (").Append(s_finalizerDrainInterval.TotalSeconds.ToString("F0")).Append("s interval, Gen2: ").Append(gen2Ready ? "Yes" : "No").Append(")\n");

                    // Memory
                    sb.Append('\n')
                      .Append("─── Memory ────────────────────────────────────────────────────────\n")
                      .Append(" Private : ").Append((privateBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB    Working Set: ").Append((workingSet / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                      .Append(" Managed : ").Append((totalManaged / 1024.0 / 1024.0).ToString("F2")).Append(" MB    Native (est): ").Append((nativeEstimate / 1024.0 / 1024.0).ToString("F2")).Append(" MB    Allocated: ").Append((GC.GetTotalAllocatedBytes(false) / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n");

                    // GC
                    sb.Append('\n')
                      .Append("─── GC ────────────────────────────────────────────────────────────\n")
                      .Append(" Heap: ").Append((gcMemInfo.HeapSizeBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB  Frag: ").Append((gcMemInfo.FragmentedBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB  Committed: ").Append((gcMemInfo.TotalCommittedBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB  Promoted: ").Append((gcMemInfo.PromotedBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n")
                      .Append(" Pinned: ").Append(gcMemInfo.PinnedObjectsCount).Append("  Finalization: ").Append(gcMemInfo.FinalizationPendingCount).Append('\n');

                    // Per-generation sizes on one line
                    var genInfo = gcMemInfo.GenerationInfo;
                    sb.Append(" Gens (Size/Frag MB):");
                    for (int i = 0; i < genInfo.Length; i++)
                    {
                        var gen = genInfo[i];
                        sb.Append("  ").Append(i).Append(':').Append((gen.SizeAfterBytes / 1024.0 / 1024.0).ToString("F2")).Append('/').Append((gen.FragmentationAfterBytes / 1024.0 / 1024.0).ToString("F2"));
                    }
                    sb.Append('\n');

                    sb.Append(" Collections (0/1/2): ").Append(GC.CollectionCount(0)).Append(" / ").Append(GC.CollectionCount(1)).Append(" / ").Append(GC.CollectionCount(2))
                      .Append("   Pause: ").Append(GC.GetTotalPauseDuration().TotalMilliseconds.ToString("F1")).Append(" ms")
                      .Append("   Load: ").Append((gcMemInfo.MemoryLoadBytes / 1024.0 / 1024.0).ToString("F2")).Append(" / ").Append((gcMemInfo.HighMemoryLoadThresholdBytes / 1024.0 / 1024.0).ToString("F2")).Append(" MB\n");

                    // ThreadPool & Process
                    ThreadPool.GetAvailableThreads(out int workersAvail, out int ioAvail);
                    ThreadPool.GetMinThreads(out int workersMin, out int ioMin);
                    ThreadPool.GetMaxThreads(out int workersMax, out int ioMax);
                    sb.Append('\n')
                      .Append("─── ThreadPool & Process ──────────────────────────────────────────\n")
                      .Append(" Workers (avail/min/max): ").Append(workersAvail).Append(" / ").Append(workersMin).Append(" / ").Append(workersMax).Append('\n')
                      .Append(" IOCP    (avail/min/max): ").Append(ioAvail).Append(" / ").Append(ioMin).Append(" / ").Append(ioMax).Append('\n')
                      .Append(" Pending: ").Append(ThreadPool.PendingWorkItemCount).Append("  Threads: ").Append(ThreadPool.ThreadCount).Append("  Timers: ").Append(Timer.ActiveCount).Append("  ProcThreads: ").Append(process.Threads.Count).Append("  Handles: ").Append(process.HandleCount).Append('\n');

                    // Components
                    sb.Append('\n')
                      .Append("─── Components ────────────────────────────────────────────────────\n")
                      .Append(" Workers      : ").Append(_getWorkerState()).Append('\n')
                      .Append(" Request Queue: ").Append(_requestsQueue?.thrdSafeCount.ToString() ?? "N/A").Append('\n')
                      .Append(" Event Client : ").Append(_eventClient != null ? _eventClient.ClientType + " (" + _eventClient.Count + " / " + _options.MaxUndrainedEvents + " items, " + _eventClient.FlushedLastMinute + " flushed/min)" : "Disabled").Append('\n');

                    // Blob Storage - inline
                    sb.Append(" Blob Storage : ");
                    if (_blobWriter != null)
                    {
                        sb.Append(_blobWriter.IsInitialized ? "Initialized" : "Not Initialized")
                          .Append("  (").Append(_blobWriter.GetConnectionInfo()).Append(", Async: ").Append(_options.AsyncModeEnabled ? "On" : "Off").Append(')');
                    }
                    else
                    {
                        sb.Append("Not Configured");
                    }
                    sb.Append('\n');

                    // Backup API - inline
                    sb.Append(" Backup API   : ");
                    if (_backupAPIService != null)
                    {
                        var eventStats = _backupAPIService.GetEventStatistics();
                        var errorStats = _backupAPIService.GetErrorStatistics();
                        var eventsLast10Min = eventStats.Values.Sum();
                        var errorsLast10Min = errorStats.Values.Sum();
                        var totalAttempts = eventsLast10Min + errorsLast10Min;
                        var errorRate = totalAttempts > 0 ? (double)errorsLast10Min / totalAttempts * 100 : 0;
                        sb.Append("Enabled   Events(1/5/10m): ").Append(eventStats[0]).Append('/').Append(eventStats.Take(5).Sum(x => x.Value)).Append('/').Append(eventsLast10Min)
                          .Append("  Errors: ").Append(errorStats[0]).Append('/').Append(errorStats.Take(5).Sum(x => x.Value)).Append('/').Append(errorsLast10Min)
                          .Append("  Rate: ").Append(errorRate.ToString("F2")).Append('%');
                    }
                    else
                    {
                        sb.Append("Disabled");
                    }
                    sb.Append('\n');

                    // Service Bus - inline
                    sb.Append(" Service Bus  : ");
                    if (_serviceBusRequestService != null)
                    {
                        var sbStats = _serviceBusRequestService.GetStatistics();
                        sb.Append(sbStats.isEnabled ? "Enabled" : "Disabled")
                          .Append("  Msgs: ").Append(sbStats.totalMessages).Append("  Batches: ").Append(sbStats.totalBatches).Append("  Depth: ").Append(sbStats.queueDepth);
                    }
                    else
                    {
                        sb.Append("Not Configured");
                    }
                    sb.Append('\n');

                    // Backend hosts
                    sb.Append('\n')
                      .Append("─── Backend Hosts ─────────────────────────────────────────────────\n")
                      .Append(" Poller Interval: ").Append(_options.PollInterval).Append(" ms\n");
                    var hosts = _backends.GetHosts();
                    if (hosts.Count > 0)
                    {
                        foreach (var host in hosts)
                        {
                            sb.Append(' ').Append(host.Host).Append("  Status: ").Append(host.GetStatus(out int calls, out int errorCalls, out double average)).Append('\n');
                        }
                    }
                    else
                    {
                        sb.Append(" No Hosts\n");
                    }
                    sb.Append("═══════════════════════════════════════════════════════════════════\n");

                    probeMessage = sb.ToString();
                }
                break;
        }
    }

    public void RunPeriodicGC()
    {
        // Periodically drain finalizers to release native memory from recycled HTTP connections.
        // Only acts when a Gen2 GC has naturally occurred since the last drain — no forced collection.
        var gen2Count = GC.CollectionCount(2);
        if (gen2Count > _lastGen2Count && DateTime.UtcNow - _lastFinalizerDrain >= s_finalizerDrainInterval)
        {
            GC.Collect(2, GCCollectionMode.Optimized, false); // non-blocking, hint only
            GC.WaitForPendingFinalizers();
            _lastGen2Count = gen2Count;
            _lastFinalizerDrain = DateTime.UtcNow;
        }

    }

    // Method to get overall health status for probes, used by ProbeServer
    // Returns a tuple of (startupStatus, readinessStatus, activeUndrainedEvents) for more detailed monitoring
    public (HealthStatusEnum, HealthStatusEnum, int) GetStatus()
    {
        int hostCount = _backends.ActiveHostCount();
        bool hasFailed = _backends.CheckFailedStatusAsync(true).Result; // this call will not block
        bool profilesReady = _userProfileService?.ServiceIsReady() ?? true; // if user profile service is not configured, consider it ready

        int activeEvents = _eventClient?.Count ?? 0;
        bool tooManyEvents = activeEvents > _options.MaxUndrainedEvents;
        bool eventsAreHealthy = _eventClient?.IsHealthy() == true;
        var isReady = IsReadyToWork && backendsStarted && profilesReady && !tooManyEvents;

        if (isReady && firstHealthCheck)
        {
            _logger.LogInformation("[-READY-] ✓ Workers ready, Profiles ready, Backends ready");
            firstHealthCheck = false;
        }

        // Debug logging - remove after fixing
        // Console.WriteLine($"[STARTUP-DEBUG] IsReadyToWork={isReady}, hasProfile={_userProfileService != null}, hostCount={hostCount}, hasFailed={hasFailed}, activeEvents={activeEvents}");

        if (!isReady)
        {
            return (HealthStatusEnum.StartupZeroHosts, HealthStatusEnum.ReadinessZeroHosts, activeEvents);
        }

        if (hostCount == 0)
        {
            return (HealthStatusEnum.StartupZeroHosts, HealthStatusEnum.ReadinessZeroHosts, activeEvents);
        }

        if (hasFailed)
        {
            return (HealthStatusEnum.StartupFailedHosts, HealthStatusEnum.ReadinessFailedHosts, activeEvents);
        }

        if (!eventsAreHealthy)
        {
            return (HealthStatusEnum.StartupFailedHosts, HealthStatusEnum.ReadinessFailedHosts, activeEvents);
        }

        return (HealthStatusEnum.StartupReady, HealthStatusEnum.ReadinessReady, activeEvents);
    }
}
