using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;


using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Backend;

public class CircuitBreaker : ICircuitBreaker, IDisposable
{
    static ProxyConfig _options = null!;
    private ConcurrentQueue<DateTime> hostFailureTimes2 = new();
    private int _failureThreshold;
    private  int _failureTimeFrame;
    private HashSet<int> _allowableCodes = null!;
    private string _allowableCodesLog = "";
    private readonly ILogger<CircuitBreaker> _logger;
    private readonly bool _isParent;
    
    // Existing global counters describe child (per-host) circuit breakers.
    private static int _totalCircuitBreakersCount = 0;
    private static int _blockedCircuitBreakersCount = 0;
    private static int _totalParentCircuitBreakersCount = 0;
    private static int _blockedParentCircuitBreakersCount = 0;
    private static readonly ConcurrentDictionary<CircuitBreaker, byte> s_allCircuitBreakers = new();
    private readonly ProxyEvent _circuitBreakerEvent = new ProxyEvent(4);  // Code, Time, Success, Count
    
    // Instance state tracking
    private volatile bool _isCurrentlyBlocked = false;
    private volatile bool _isDeregistered = false;

    private int count_50percent;
    private int count_60percent;
    private int count_70percent;
    private int count_80percent;
    private int count_90percent;
    private static int delay_50percent = 100;
    private static int delay_60percent = 200;
    private static int delay_70percent = 300;
    private static int delay_80percent = 400;
    private static int delay_90percent = 500;
    private static int max_delay = 1000;

    private static readonly System.Threading.Timer _timer = new(
        OnTimerTick,
        null,
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(500));
    private static int s_timerRunning;

    private long _nextRetryDeadlineUtcTicks;

    
    public string ID { get; set; } = "";
    public bool TrackRetryAfter { get; set; } = false;

    // if TrackRetryAfter is enabled, this property determines the next retry deadline.
    public DateTime NextRetryDeadlineUtc { get; set; }

    public CircuitBreaker(IOptions<ProxyConfig> options, ILogger<CircuitBreaker> logger, bool isParent = false)
    {
        ArgumentNullException.ThrowIfNull(options?.Value, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        var backendOptions = options.Value;
        _logger = logger;
        _isParent = isParent;
        _options = backendOptions;

        InitVars();

        if (_isParent)
        {
            Interlocked.Increment(ref _totalParentCircuitBreakersCount);
        }
        else
        {
            Interlocked.Increment(ref _totalCircuitBreakersCount);
        }
        
        if (string.IsNullOrEmpty(ID))
        {
            ID = Guid.NewGuid().ToString();
        }

        var role = _isParent ? "Parent" : "Child";
        var roleTotal = _isParent ? _totalParentCircuitBreakersCount : _totalCircuitBreakersCount;
        _logger.LogDebug("[STARTUP] {Role} circuit breaker {ID} initialized with threshold: {Threshold}, timeframe: {TimeFrame}s. Role total: {Total}",
            role, ID, _failureThreshold, _failureTimeFrame, roleTotal);
        s_allCircuitBreakers.TryAdd(this, 0);
    }

    private static void OnTimerTick(object? state)
    {
        if (Interlocked.Exchange(ref s_timerRunning, 1) != 0) return;

        try
        {
            var now = DateTime.UtcNow;
            int blockedChildCount = 0;
            int blockedParentCount = 0;
            // Enumerate the dictionary directly — .Keys allocates a snapshot list every tick.
            foreach (var kvp in s_allCircuitBreakers)
            {
                var cb = kvp.Key;
                if (!cb.cleanQueue(now)) continue;

                if (cb._isParent)
                {
                    blockedParentCount++;
                }
                else
                {
                    blockedChildCount++;
                }
            }

            Volatile.Write(ref _blockedCircuitBreakersCount, blockedChildCount);
            Volatile.Write(ref _blockedParentCircuitBreakersCount, blockedParentCount);
        }
        finally
        {
            Volatile.Write(ref s_timerRunning, 0);
        }
    }

    public void InitVars()
    {
        _failureThreshold = _options.CircuitBreakerErrorThreshold;
        _failureTimeFrame = _options.CircuitBreakerTimeslice;
        _allowableCodes = new HashSet<int>(_options.AcceptableStatusCodes ?? new[] { 200, 401, 403, 408, 410, 412, 417, 400 });
        _allowableCodesLog = string.Join(",", _allowableCodes);

        count_50percent = (int)(_failureThreshold * 0.5);
        count_60percent = (int)(_failureThreshold * 0.6);
        count_70percent = (int)(_failureThreshold * 0.7);
        count_80percent = (int)(_failureThreshold * 0.8);
        count_90percent = (int)(_failureThreshold * 0.9);
    }

    private bool cleanQueue(DateTime now)
    {
        if (_isDeregistered) return false;

        while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= _failureTimeFrame)
        {
            hostFailureTimes2.TryDequeue(out var _);
        }

        bool wasBlocked = _isCurrentlyBlocked;
        bool isCurrentlyFailed = hostFailureTimes2.Count >= _failureThreshold;
        _isCurrentlyBlocked = isCurrentlyFailed;

        if (isCurrentlyFailed && !wasBlocked)
        {
            _logger.LogCritical("[CB LOCK] ID: {ID}", ID);
        }
        else if (!isCurrentlyFailed && wasBlocked)
        {
            _logger.LogCritical("[CB UNLOCK] ID: {ID}", ID);
        }

        return !_isDeregistered && isCurrentlyFailed;
    }

    public void TrackStatus(int code, bool wasFailure, string state, HttpResponseHeaders? responseHeaders = null)
    {
        if (_allowableCodes.Contains(code) && !wasFailure)
        {
            return;
        }
        _logger.LogDebug("Tracking failure for circuit breaker {ID} with code {Code} : codes {codes}", ID, code, _allowableCodesLog);

        DateTime now = DateTime.UtcNow;
        hostFailureTimes2.Enqueue(now);

        long retryTicks = 0;
        if (TrackRetryAfter)
        {
            // Check response headers for Retry-After and Retry-After-Ms (.NET matches case insensitively already).
            int? retryMs = null;
            if (responseHeaders is not null)
            {
                if (responseHeaders.TryGetValues("Retry-After", out var retryAfterValues)
                    && int.TryParse(retryAfterValues.FirstOrDefault(), out var retryAfterSeconds))
                {
                    retryMs = retryAfterSeconds * 1000;
                }

                if (responseHeaders.TryGetValues("Retry-After-Ms", out var retryAfterMsValues)
                    && int.TryParse(retryAfterMsValues.FirstOrDefault(), out var retryAfterMs))
                {
                    retryMs = retryMs.HasValue ? Math.Max(retryMs.Value, retryAfterMs) : retryAfterMs;
                }
            }

            if (retryMs.HasValue)
            {
                var retryDeadline = now.AddMilliseconds(retryMs.Value + Constants.RetryAfterJitterMaxMs);
                NextRetryDeadlineUtc = retryDeadline;
                retryTicks = retryDeadline.Ticks;
            }
        }

        var failureCount = hostFailureTimes2.Count;

        // If no more failures arrive, the breaker closes when enough oldest
        // entries expire to leave fewer than the configured threshold.
        var entriesToRemove = failureCount - _failureThreshold + 1;
        long cbDeadlineTicks = 0;
        if (entriesToRemove > 0)
        {
            foreach (var failureTime in hostFailureTimes2)
            {
                if (--entriesToRemove != 0) continue;

                cbDeadlineTicks = failureTime.AddSeconds(_failureTimeFrame).Ticks;
                break;
            }
        }

        if (retryTicks > 0 && (cbDeadlineTicks <= 0 || retryTicks > cbDeadlineTicks))
        {
            cbDeadlineTicks = retryTicks;
        }

        if (cbDeadlineTicks > 0)
        {
            NextRetryDeadlineUtc = new DateTime(cbDeadlineTicks, DateTimeKind.Utc);

            long currentDeadline;
            do
            {
                currentDeadline = Volatile.Read(ref _nextRetryDeadlineUtcTicks);
                if (currentDeadline >= cbDeadlineTicks) break;
            }
            while (Interlocked.CompareExchange(
                ref _nextRetryDeadlineUtcTicks,
                cbDeadlineTicks,
                currentDeadline) != currentDeadline);
        }

        // Reuse and clear the circuit breaker event instance
        _circuitBreakerEvent.Clear();
        _circuitBreakerEvent.Type = EventType.CircuitBreakerError;
        _circuitBreakerEvent["Code"] = code.ToString();
        _circuitBreakerEvent["Time"] = now.ToString();
        _circuitBreakerEvent["Success"] = (!wasFailure).ToString();
        _circuitBreakerEvent["Count"] = failureCount.ToString();

        _circuitBreakerEvent.SendEvent();

        _logger.LogDebug("[CB-ERROR] cbid-{ID}, Error code: {Code}, Timeslice Errors: {Count}, State: {State}", 
            ID, code, failureCount, state);
    }


    /// <summary>
    /// Estimates the milliseconds until enough failures expire for the circuit breaker to close.
    /// </summary>
    public int GetMsToNextRetry()
    {
        // Escalate backpressure at the parent once every child backend is blocked.
        if (_isParent && AreAllCircuitBreakersBlocked())
        {
            return 2 * max_delay;
        }

        if (_failureThreshold <= 0)
        {
            return 0;
        }

        var remainingTicks = Volatile.Read(ref _nextRetryDeadlineUtcTicks) - DateTime.UtcNow.Ticks;
        if (remainingTicks <= 0)
        {
            return 0;
        }

        var remainingMs = ((remainingTicks - 1) / TimeSpan.TicksPerMillisecond) + 1;
        return (int)Math.Min(int.MaxValue, remainingMs);
    }

    // returns the milliseconds of backpressure delay if the service is in failure state
    public int GetBackpressureDelay()
    {
        int count = hostFailureTimes2.Count;

        // evals to efficient comparison
        return count switch
        {
            _ when count < count_50percent => 0,
            _ when count < count_60percent => delay_50percent,
            _ when count < count_70percent => delay_60percent,
            _ when count < count_80percent => delay_70percent,
            _ when count < count_90percent => delay_80percent,
            _ when count < _failureThreshold => delay_90percent,
            _ => max_delay
        };
    }

    /// <summary>
    /// Removes this instance from the global circuit-breaker counters.
    /// Safe to call multiple times — only the first call has any effect.
    /// </summary>
    public void Deregister()
    {
        if (!s_allCircuitBreakers.TryRemove(this, out _)) return;

        _isDeregistered = true;
        _isCurrentlyBlocked = false;
        if (_isParent)
        {
            Interlocked.Decrement(ref _totalParentCircuitBreakersCount);
        }
        else
        {
            Interlocked.Decrement(ref _totalCircuitBreakersCount);
        }

        var role = _isParent ? "Parent" : "Child";
        var roleTotal = _isParent ? _totalParentCircuitBreakersCount : _totalCircuitBreakersCount;
        var roleBlocked = _isParent ? _blockedParentCircuitBreakersCount : _blockedCircuitBreakersCount;
        _logger.LogDebug("[CB] {Role} circuit breaker {ID} deregistered. Role total: {Total}, blocked: {Blocked}",
            role, ID, roleTotal, roleBlocked);
    }

    public void Dispose() => Deregister();

    /// <summary>
    /// Checks if all child circuit breakers globally are in a failed state
    /// </summary>
    /// <returns>True if all circuit breakers are blocked, false otherwise</returns>
    public static bool AreAllCircuitBreakersBlocked()
    {
        int total = _totalCircuitBreakersCount;
        int blocked = _blockedCircuitBreakersCount;
        
        // If there are no circuit breakers, return false
        if (total == 0)
        {
            return false;
        }
        
        // Return true only if all circuit breakers are blocked
        return blocked >= total;
    }

    /// <summary>
    /// Gets the count of child circuit breakers that are currently blocked
    /// </summary>
    /// <returns>Number of blocked circuit breakers</returns>
    public static int GetBlockedCircuitBreakersCount()
    {
        return _blockedCircuitBreakersCount;
    }

    /// <summary>
    /// Gets the total count of registered child circuit breakers
    /// </summary>
    /// <returns>Total number of circuit breakers</returns>
    public static int GetTotalCircuitBreakersCount()
    {
        return _totalCircuitBreakersCount;
    }

    /// <summary>
    /// Gets the current circuit breaker status details for logging and diagnostics
    /// </summary>
    /// <returns>A string with circuit breaker status information</returns>
    public string GetCircuitBreakerStatusString()
    {
        DateTime now = DateTime.UtcNow;
        DateTime? oldestFailure = null;
        DateTime? newestFailure = null;
        double? timeUntilOldestExpires = null;
        
        // Get the oldest and newest failure times
        if (hostFailureTimes2.TryPeek(out var oldest))
        {
            oldestFailure = oldest;
            timeUntilOldestExpires = _failureTimeFrame - (now - oldest).TotalSeconds;
            
            // Get newest failure (last item in queue)
            var allFailures = hostFailureTimes2.ToArray();
            if (allFailures.Length > 0)
            {
                newestFailure = allFailures[allFailures.Length - 1];
            }
        }
        
        TimeSpan delta = newestFailure.HasValue && oldestFailure.HasValue
            ? newestFailure.Value - oldestFailure.Value
            : TimeSpan.Zero;

        var errCnt = hostFailureTimes2.Count;
        var blocked = _isCurrentlyBlocked;
        var expIn = timeUntilOldestExpires?.ToString("F1") ?? "N/A";
        var span = delta.ToString("c");

        return $"FailureCount: {errCnt}/{_failureThreshold}, IsBlocked: {blocked}, SecondsUntilUnblock: {expIn}, OldestFailure: {span}, NewestFailure: {span}";
    }


}