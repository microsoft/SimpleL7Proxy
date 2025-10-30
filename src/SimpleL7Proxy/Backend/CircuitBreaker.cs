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

        _logger.LogDebug("Circuit breaker initialized with threshold: {Threshold}, timeframe: {TimeFrame}s", 
            _failureThreshold, _failureTimeFrame);
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
    }

    // returns true if the service is in failure state
    public bool CheckFailedStatus()
    {
        //    Console.WriteLine($"Checking failed status: {hostFailureTimes2.Count} >= {FailureThreshold}");
        if (hostFailureTimes2.Count < _failureThreshold)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        while (hostFailureTimes2.TryPeek(out var t) && (now - t).TotalSeconds >= _failureTimeFrame)
        {
            hostFailureTimes2.TryDequeue(out var _);
        }
        return hostFailureTimes2.Count >= _failureThreshold;

    }

}