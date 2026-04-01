using System.Collections.Concurrent;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;


using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Backend;

public class CircuitBreaker : ICircuitBreaker
{
    static ProxyConfig _options = null!;
    private ConcurrentQueue<DateTime> hostFailureTimes2 = new();
    private int _failureThreshold;
    private  int _failureTimeFrame;
    private HashSet<int> _allowableCodes = null!;
    private readonly ILogger<CircuitBreaker> _logger;
    
    // Global counters using Interlocked operations
    private static int _totalCircuitBreakersCount = 0;
    private static int _blockedCircuitBreakersCount = 0;
    private readonly ProxyEvent _circuitBreakerEvent = new ProxyEvent(4);  // Code, Time, Success, Count
    
    // Instance state tracking
    private bool _isCurrentlyBlocked = false;
    private bool _isDeregistered = false;

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

    
    public string ID { get; set; } = "";

    public CircuitBreaker(IOptions<ProxyConfig> options, ILogger<CircuitBreaker> logger)
    {
        ArgumentNullException.ThrowIfNull(options?.Value, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        var backendOptions = options.Value;
        _logger = logger;
        _options = backendOptions;

        InitVars();

        // Register this circuit breaker globally
        Interlocked.Increment(ref _totalCircuitBreakersCount);
        
        if (string.IsNullOrEmpty(ID))
        {
            ID = Guid.NewGuid().ToString();
        }

        _logger.LogDebug("[INIT] Circuit breaker {ID} initialized with threshold: {Threshold}, timeframe: {TimeFrame}s. Total circuit breakers: {Total}", 
            ID, _failureThreshold, _failureTimeFrame, _totalCircuitBreakersCount);
    }

    public void InitVars()
    {
        _failureThreshold = _options.CircuitBreakerErrorThreshold;
        _failureTimeFrame = _options.CircuitBreakerTimeslice;
        _allowableCodes = new HashSet<int>(_options.AcceptableStatusCodes ?? new[] { 200, 401, 403, 408, 410, 412, 417, 400 });

        count_50percent = (int)(_failureThreshold * 0.5);
        count_60percent = (int)(_failureThreshold * 0.6);
        count_70percent = (int)(_failureThreshold * 0.7);
        count_80percent = (int)(_failureThreshold * 0.8);
        count_90percent = (int)(_failureThreshold * 0.9);
    }

    public void TrackStatus(int code, bool wasFailure, string state)
    {
        if (_allowableCodes.Contains(code) && !wasFailure)
        {
            return;
        }
        _logger.LogCritical("Tracking failure for circuit breaker {ID} with code {Code} : codes {codes}", ID, code, string.Join(",", _allowableCodes));

        DateTime now = DateTime.UtcNow;

        // truncate older entries
        while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= _failureTimeFrame)
        {
            hostFailureTimes2.TryDequeue(out var _);
        }

        hostFailureTimes2.Enqueue(now);
        // Reuse and clear the circuit breaker event instance
        _circuitBreakerEvent.Clear();
        _circuitBreakerEvent.Type = EventType.CircuitBreakerError;
        _circuitBreakerEvent["Code"] = code.ToString();
        _circuitBreakerEvent["Time"] = now.ToString();
        _circuitBreakerEvent["Success"] = (!wasFailure).ToString();
        _circuitBreakerEvent["Count"] = hostFailureTimes2.Count.ToString();

        _circuitBreakerEvent.SendEvent();

        _logger.LogCritical("[CB-ERROR] cbid-{ID}, Error code: {Code}, Timeslice Errors: {Count}, State: {State}", 
            ID, code, hostFailureTimes2.Count, state);
    }


    // returns true if the service is in failure state
    public async Task<bool> CheckFailedStatusAsync(bool nosleep=false)
    {
        int count = hostFailureTimes2.Count;
        //    Console.WriteLine($"Checking failed status: {hostFailureTimes2.Count} >= {FailureThreshold}");
        if (count < _failureThreshold)
        {
            if ( count >= count_50percent && !nosleep )
            {
                int delay = count >= count_90percent ? delay_90percent :
                            count >= count_80percent ? delay_80percent :
                            count >= count_70percent ? delay_70percent :
                            count >= count_60percent ? delay_60percent : delay_50percent;

                _logger.LogWarning("[CB-DELAY] Circuit breaker {ID} is experiencing elevated error rates. Count: {Count}, Introducing delay: {Delay}ms", 
                    ID, count, delay);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            
            // If we were previously blocked but now we're not, decrement the blocked count
            if (_isCurrentlyBlocked)
            {
                _isCurrentlyBlocked = false;
                Interlocked.Decrement(ref _blockedCircuitBreakersCount);
                _logger.LogDebug("Circuit breaker {ID} unblocked. Blocked count: {BlockedCount}", 
                    ID, _blockedCircuitBreakersCount);
            }
            return false;
        }

        DateTime now = DateTime.UtcNow;
        while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= _failureTimeFrame)
        {
            hostFailureTimes2.TryDequeue(out var _);
        }
        
        bool isCurrentlyFailed = hostFailureTimes2.Count >= _failureThreshold;
        
        // Update global blocked count based on state change
        if (isCurrentlyFailed && !_isCurrentlyBlocked)
        {
            _isCurrentlyBlocked = true;
            Interlocked.Increment(ref _blockedCircuitBreakersCount);
            _logger.LogCritical("[CB LOCK] ID: {ID}, Count: {BlockedCount}", 
                ID, _blockedCircuitBreakersCount);
        }
        else if (!isCurrentlyFailed && _isCurrentlyBlocked)
        {
            _isCurrentlyBlocked = false;
            Interlocked.Decrement(ref _blockedCircuitBreakersCount);
            _logger.LogCritical("[CB UNLOCK] ID: {ID}, Count: {BlockedCount}", 
                ID, _blockedCircuitBreakersCount);
        }
        
        return isCurrentlyFailed;
    }

    /// <summary>
    /// Removes this instance from the global circuit-breaker counters.
    /// Safe to call multiple times — only the first call has any effect.
    /// </summary>
    public void Deregister()
    {
        if (_isDeregistered) return;
        _isDeregistered = true;

        Interlocked.Decrement(ref _totalCircuitBreakersCount);

        if (_isCurrentlyBlocked)
        {
            _isCurrentlyBlocked = false;
            Interlocked.Decrement(ref _blockedCircuitBreakersCount);
        }

        _logger.LogDebug("[CB] Circuit breaker {ID} deregistered. Total: {Total}, Blocked: {Blocked}",
            ID, _totalCircuitBreakersCount, _blockedCircuitBreakersCount);
    }

    /// <summary>
    /// Checks if all circuit breakers globally are in a failed state
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
    /// Gets the count of circuit breakers that are currently blocked
    /// </summary>
    /// <returns>Number of blocked circuit breakers</returns>
    public static int GetBlockedCircuitBreakersCount()
    {
        return _blockedCircuitBreakersCount;
    }

    /// <summary>
    /// Gets the total count of registered circuit breakers
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

        var d= new Dictionary<string, string>
        {
            ["cbId"] = ID,
            ["errCnt"] = hostFailureTimes2.Count.ToString(),
            ["errMax"] = _failureThreshold.ToString(),
            ["winSec"] = _failureTimeFrame.ToString(),
            ["blocked"] = _isCurrentlyBlocked.ToString(),
            // ["failed"] = CheckFailedStatus().ToString(),
            ["expIn"] = timeUntilOldestExpires?.ToString("F1") ?? "N/A",
            ["span"] = delta.ToString("c")
        };

        return $"FailureCount: {d["errCnt"]}/{d["errMax"]}, IsBlocked: {d["blocked"]}, SecondsUntilUnblock: {d["expIn"]}, OldestFailure: {d["span"]}, NewestFailure: {d["span"]}";
    }


}