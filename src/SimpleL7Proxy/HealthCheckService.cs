using Microsoft.Extensions.Logging;
using System.Text;
using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Async.BlobStorage;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Async.ServiceBus.SBQueue;
using SimpleL7Proxy.Async.ServiceBus.SBTopic;
using SimpleL7Proxy.Proxy;

using Shared.HealthProbe;

namespace SimpleL7Proxy;

/// <summary>Serves probe endpoints (/health, /readiness, /startup, /liveness) and tracks per-worker state for diagnostics. Hot path — perf matters.</summary>
public class HealthCheckService
{
    private readonly IEndpointMonitorService _backends;
    private readonly ReadinessRegistry _readiness;
    private static ProxyConfig _options=null!;
    private readonly IConcurrentPriQueue<RequestData>? _requestsQueue;
    private readonly IUserPriorityService? _userPriority;
    private readonly IEventClient? _eventClient;
    private readonly ISBQueueService? _sbQueueService;
    private readonly ISBTopicService? _sbTopicService;
    private readonly IQueuedBlobWriter? _blobWriter;
    private readonly BlobWorkerPump? _blobWriteQueue;
    private readonly AppConfigService _appConfigService;
    private readonly Func<string> _getWorkerState;
    private readonly ILogger<HealthCheckService> _logger;

    // Pre-allocated response buffer to avoid per-probe allocations.
    private readonly StringBuilder _stringBuilder;

    // Set true once ReadinessRegistry signals all participants ready.
    private volatile bool _systemReady;

    // Worker state tracking — flat fields for hot-path speed.
    private static int _activeWorkers = 0;

    // Current state per worker id.
    private static readonly ConcurrentDictionary<int, WorkerState?> _workerCurrentState = new();

    // Per-state counters (updated via Interlocked).
    private static int _dequeueingCount = 0;
    private static int _preProcessingCount = 0;
    private static int _proxyingCount = 0;
    private static int _sendingCount = 0;
    private static int _receivingCount = 0;
    private static int _writingCount = 0;
    private static int _reportingCount = 0;
    private static int _cleanupCount = 0;

    public static int ActiveWorkers => _activeWorkers;

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
        AppConfigService appConfigService,
        ReadinessRegistry readiness,
        ISBTopicService? sbTopicService = null,
        IQueuedBlobWriter? blobWriter = null,
        BlobWorkerPump? blobWriteQueue = null,
        ISBQueueService? sbQueueService = null)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _appConfigService = appConfigService ?? throw new ArgumentNullException(nameof(appConfigService));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _requestsQueue = requestsQueue;
        _userPriority = userPriority;
        _eventClient = eventClient;
        _sbTopicService = sbTopicService;
        _blobWriter = blobWriter;
        _blobWriteQueue = blobWriteQueue;
        _sbQueueService = sbQueueService;
        _getWorkerState = GetWorkerState;
        _logger = logger;

        // Pre-allocate StringBuilder to reduce allocations
        _stringBuilder = new StringBuilder(512);
        s_finalizerDrainInterval = _options.GC2InternalSecs > 0 ? TimeSpan.FromSeconds(_options.GC2InternalSecs) : TimeSpan.FromMinutes(15);

        // Cache readiness as a single bool so the hot probe path skips per-call evaluation.
        _ = _readiness.WaitForReadyAsync().ContinueWith(
            _ =>
            {
                var readyList = string.Join(", ", _readiness.Snapshot()
                    .Where(s => s.IsReady)
                    .Select(s => s.Participant));
                _logger.LogInformation("[-READY-] {Participants}", readyList);
                _systemReady = true;
            },
            TaskScheduler.Default);
    }

    /// <summary>Returns a compact one-line snapshot of worker count and per-state counters.</summary>
    public static string GetWorkerState()
    {
        return $"Count: {_activeWorkers} States: [ deq-{_dequeueingCount} pre-{_preProcessingCount} prxy-{_proxyingCount} -[snd-{_sendingCount} rcv-{_receivingCount}]-  wr-{_writingCount} rpt-{_reportingCount} cln-{_cleanupCount} ]";
    }

    /// <summary>Enters <paramref name="newState"/> for <paramref name="workerId"/>, auto-exiting any previous state.</summary>
    public static void EnterState(int workerId, WorkerState newState)
    {
        if ( !_options.TrackWorkers) return;

        // Atomically swap the worker's current state, exiting the old one in the update callback.
        var oldState = _workerCurrentState.AddOrUpdate(
            workerId,
            newState,
            (_, currentState) =>
            {
                if (currentState.HasValue)
                {
                    ExitStateInternal(currentState.Value);
                }
                return newState;
            });

        // First-seen worker: oldState == newState (just set); otherwise old was already exited above.
        if (oldState.Equals(newState))
        {
            EnterStateInternal(newState);
        }
        else
        {
            EnterStateInternal(newState);
        }
    }

    /// <summary>Increments the counter for <paramref name="state"/>.</summary>
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

    /// <summary>Decrements the counter for <paramref name="state"/>.</summary>
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

    /// <summary>Increments and returns the active-worker count.</summary>
    public static int IncrementActiveWorkers(int totalWorkers)
    {
        _ = totalWorkers;
        return Interlocked.Increment(ref _activeWorkers);
    }

    /// <summary>Decrements active workers and exits <paramref name="workerId"/>'s current state, if any.</summary>
    public static void DecrementActiveWorkers(int workerId)
    {
        Interlocked.Decrement(ref _activeWorkers);

        // Exit the worker's current state if it has one.
        if (_workerCurrentState.TryRemove(workerId, out var currentState) && currentState.HasValue)
        {
            ExitStateInternal(currentState.Value);
        }
    }

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
            .Append("  NextGC: ").Append(gcRemaining > TimeSpan.Zero ? gcRemaining.TotalSeconds.ToString("F0") + "s" : "ready")
            .Append("  AppConfig Status: ").Append(_appConfigService.Status());

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
                    // Pre-allocated StringBuilder, guarded by lock — reused across probe calls.
                    lock (_stringBuilder)
                    {
                        _stringBuilder.Clear();

                        _stringBuilder
                            .Append("═════════════════ SimpleL7Proxy Health Status ══════════════════\n")
                            .Append(' ').Append(shared).Append('\n');

                        // Probes
                        var (startupStatus, readinessStatus, undrainedEvents, blobQueueDepth) = GetStatus();
                        _stringBuilder
                            .Append('\n')
                            .Append("─── Probes ────────────────────────────────────────────────────\n")
                            .Append(" /startup   : ").Append(startupStatus == HealthStatusEnum.StartupReady ? "200 OK" : "503 " + startupStatus).Append('\n')
                            .Append(" /readiness : ").Append(readinessStatus == HealthStatusEnum.ReadinessReady ? "200 OK" : "503 " + readinessStatus).Append('\n')
                            .Append(" Undrained  : ").Append(undrainedEvents).Append(" / ").Append(_options.MaxUndrainedEvents).Append('\n')
                            .Append(" Blob Queue : ").Append(blobQueueDepth).Append(" / ").Append(_options.AsyncBlobMaxQueue).Append('\n');

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
                            _stringBuilder.Append(" SBQueue Service : ").Append(_sbQueueService != null ? "Enabled" : "Disabled").Append('\n')
                                .Append(" Service Bus     : ").Append(_sbTopicService != null ? "Enabled" : "Disabled").Append('\n')
                                .Append(" Blob Storage    : ").Append(_blobWriter != null ? "Enabled" : "Disabled").Append('\n');
                        }
                        else
                        {
                            _stringBuilder.Append(" Async Services  : Off (SBQueue, Service Bus, Blob Storage)\n");
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

                    // SBQueue service - inline
                    sb.Append(" SBQueue      : ");
                    if (_sbQueueService != null)
                    {
                        var eventStats = _sbQueueService.GetEventStatistics();
                        var errorStats = _sbQueueService.GetErrorStatistics();
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
                    if (_sbTopicService != null)
                    {
                        var sbStats = _sbTopicService.GetStatistics();
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
                            sb.Append(' ').Append(host.ToString())
                              .Append("  Status: ").Append(host.GetStatus(out int calls, out int errorCalls, out double average)).Append('\n');
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
        // Drain finalizers periodically to release native memory from recycled HTTP connections — only when a Gen2 GC has naturally occurred (no forced collection).
        var gen2Count = GC.CollectionCount(2);
        if (gen2Count > _lastGen2Count && DateTime.UtcNow - _lastFinalizerDrain >= s_finalizerDrainInterval)
        {
            GC.Collect(2, GCCollectionMode.Optimized, false); // non-blocking, hint only
            GC.WaitForPendingFinalizers();
            _lastGen2Count = gen2Count;
            _lastFinalizerDrain = DateTime.UtcNow;
        }

    }

    // Overall health status for probes (used by ProbeServer). Returns (startupStatus, readinessStatus, activeUndrainedEvents, blobQueueDepth).
    public (HealthStatusEnum, HealthStatusEnum, int, int) GetStatus()
    {
        int hostCount = _backends.ActiveHostCount();
        bool hasFailed = _backends.CheckFailedStatusAsync(true).Result;

        int activeEvents = _eventClient?.Count ?? 0;
        bool tooManyEvents = activeEvents > _options.MaxUndrainedEvents;
        bool eventsAreHealthy = _eventClient?.IsHealthy() == true;
        int blobQueueDepth = _blobWriteQueue?.QueueDepth ?? 0;
        bool blobQueueHealthy = blobQueueDepth <= _options.AsyncBlobMaxQueue;
        var isReady = _systemReady && !tooManyEvents && blobQueueHealthy;

        if (!isReady)
        {
            return (HealthStatusEnum.StartupZeroHosts, HealthStatusEnum.ReadinessZeroHosts, activeEvents, blobQueueDepth);
        }

        if (hostCount == 0)
        {
            return (HealthStatusEnum.StartupZeroHosts, HealthStatusEnum.ReadinessZeroHosts, activeEvents, blobQueueDepth);
        }

        if (hasFailed)
        {
            return (HealthStatusEnum.StartupFailedHosts, HealthStatusEnum.ReadinessFailedHosts, activeEvents, blobQueueDepth);
        }

        if (!eventsAreHealthy)
        {
            return (HealthStatusEnum.StartupFailedHosts, HealthStatusEnum.ReadinessFailedHosts, activeEvents, blobQueueDepth);
        }

        return (HealthStatusEnum.StartupReady, HealthStatusEnum.ReadinessReady, activeEvents, blobQueueDepth);
    }
}
