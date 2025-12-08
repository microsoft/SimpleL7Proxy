using System.Collections.Concurrent;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;


using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Backend;

public class CircuitBreaker : ICircuitBreaker
{
    private ConcurrentQueue<DateTime> hostFailureTimes2 = new();
    private readonly int _failureThreshold;
    private readonly int _failureTimeFrame;
    private readonly int[] _allowableCodes;
    private readonly ILogger<CircuitBreaker> _logger;
    
    // Global counters using Interlocked operations
    private static int _totalCircuitBreakersCount = 0;
    private static int _blockedCircuitBreakersCount = 0;
    
    // Instance state tracking
    private bool _isCurrentlyBlocked = false;
    
    public string ID { get; set; } = "";

    public CircuitBreaker(IOptions<BackendOptions> options, ILogger<CircuitBreaker> logger)
    {
        ArgumentNullException.ThrowIfNull(options?.Value, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        var backendOptions = options.Value;
        _failureThreshold = backendOptions.CircuitBreakerErrorThreshold;
        _failureTimeFrame = backendOptions.CircuitBreakerTimeslice;
        _allowableCodes = backendOptions.AcceptableStatusCodes ?? new[] { 200, 401, 403, 408, 410, 412, 417, 400 };
        _logger = logger;

        // Register this circuit breaker globally
        Interlocked.Increment(ref _totalCircuitBreakersCount);
        
        if (string.IsNullOrEmpty(ID))
        {
            ID = Guid.NewGuid().ToString();
        }

        _logger.LogDebug("[INIT] Circuit breaker {ID} initialized with threshold: {Threshold}, timeframe: {TimeFrame}s. Total circuit breakers: {Total}", 
            ID, _failureThreshold, _failureTimeFrame, _totalCircuitBreakersCount);
    }

    public void TrackStatus(int code, bool wasException)
    {
        if (_allowableCodes.Contains(code) && !wasException)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        // truncate older entries
        while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= _failureTimeFrame)
        {
            hostFailureTimes2.TryDequeue(out var _);
        }

        hostFailureTimes2.Enqueue(now);
        ProxyEvent logerror = new ProxyEvent()
        {
            ["ID"] = ID,
            ["Code"] = code.ToString(),
            ["Time"] = now.ToString(),
            ["WasException"] = wasException.ToString(),
            ["Count"] = hostFailureTimes2.Count.ToString(),
            Type = EventType.CircuitBreakerError
        };

        logerror.SendEvent();

        _logger.LogCritical("[ERROR] Circuit breaker {ID} tracked error with code {Code} at {Time}. Total errors in timeslice: {Count}", 
            ID, code, now, hostFailureTimes2.Count);
    }

    // returns true if the service is in failure state
    public bool CheckFailedStatus()
    {
        //    Console.WriteLine($"Checking failed status: {hostFailureTimes2.Count} >= {FailureThreshold}");
        if (hostFailureTimes2.Count < _failureThreshold)
        {
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
            _logger.LogDebug("Circuit breaker {ID} is now blocked. Blocked count: {BlockedCount}", 
                ID, _blockedCircuitBreakersCount);
        }
        else if (!isCurrentlyFailed && _isCurrentlyBlocked)
        {
            _isCurrentlyBlocked = false;
            Interlocked.Decrement(ref _blockedCircuitBreakersCount);
            _logger.LogDebug("Circuit breaker {ID} is no longer blocked. Blocked count: {BlockedCount}", 
                ID, _blockedCircuitBreakersCount);
        }
        
        return isCurrentlyFailed;
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
    /// <returns>A dictionary with circuit breaker status information</returns>
    public Dictionary<string, string> GetCircuitBreakerStatus()
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
        
        return new Dictionary<string, string>
        {
            ["ID"] = ID,
            ["FailureCount"] = hostFailureTimes2.Count.ToString(),
            ["FailureThreshold"] = _failureThreshold.ToString(),
            ["TimeFrame"] = _failureTimeFrame.ToString(),
            ["IsBlocked"] = _isCurrentlyBlocked.ToString(),
            ["IsCurrentlyFailed"] = CheckFailedStatus().ToString(),
            ["OldestFailure"] = oldestFailure?.ToString("o") ?? "None",
            ["NewestFailure"] = newestFailure?.ToString("o") ?? "None",
            ["SecondsUntilOldestExpires"] = timeUntilOldestExpires?.ToString("F1") ?? "N/A"
        };
    }

}