using OS = System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

public abstract class BaseHostHealth
{
  public Guid guid = Guid.NewGuid();
  public HostConfig HostConfig { get; set; }

  public string Host => HostConfig.Host;
  public int Port => HostConfig.Port;
  public string Url => HostConfig.Url;
  public string IpAddr => HostConfig.IpAddr ?? HostConfig.Host;
  public string Protocol => HostConfig.Protocol;
  public double CalculatedAverageLatency { get; set; }

  private const int MaxData = 50;
  protected readonly Queue<double> _latencies = new();

  // Runtime performance tracking (separate from health checks)
  private ConcurrentQueue<double> _pxLatency = new ConcurrentQueue<double>();
  private int _errors;

  protected BaseHostHealth(HostConfig hostConfig, ILogger logger)
  {
    HostConfig = hostConfig ?? throw new ArgumentNullException(nameof(hostConfig));
    logger.LogInformation($"[CONFIG] ✓ Backend host registered: {HostConfig.Host}");
  }

  public override string ToString()
  {
    return $"{Protocol}://{Host}:{Port}";
  }

  #region Runtime Performance Tracking

  public void AddPxLatency(double latency)
  {
    _pxLatency.Enqueue(latency);
  }

  public void AddError()
  {
    Interlocked.Increment(ref _errors);
  }

  public string GetStatus(out int calls, out int errorCalls, out double average)
  {
    if (_pxLatency.Count == 0)
    {
      errorCalls = _errors;
      average = 0;
      calls = 0;
      _errors = 0;
      return " - ";
    }

    var status = _pxLatency;
    errorCalls = _errors;
    average = Math.Round(status.Average(), 3);
    calls = status.Count;

    return $" Calls: {status.Count} Err: {errorCalls} Avg: {Math.Round(status.Average(), 3)}ms";
  }

  public void ResetStatus()
  {
    _pxLatency = new ConcurrentQueue<double>();
    Interlocked.Exchange(ref _errors, 0);
  }

  #endregion

  #region Latency Tracking

  public void AddLatency(double latency)
  {
    if (_latencies.Count == MaxData)
      _latencies.Dequeue();
    _latencies.Enqueue(latency);
  }

  protected Queue<double> GetLatencies() => _latencies;

  public virtual double AverageLatency()
  {
    if (_latencies.Count == 0)
      return 0.0;
    return _latencies.Average() + (1 - SuccessRate()) * 100;
  }

  #endregion

  #region Abstract Methods for Health Checking

  /// <summary>
  /// Gets the success rate for this host. Implementation varies by host type.
  /// </summary>
  public abstract double SuccessRate();

  /// <summary>
  /// Records the result of a health check or operational call.
  /// </summary>
  public abstract void AddCallSuccess(bool success);

  /// <summary>
  /// Indicates whether this host supports health probing.
  /// </summary>
  public abstract bool SupportsProbing { get; }

  #endregion
  // Method to track the success of a call
  // public void AddCallSuccess(bool success)
  // {
  //   // If there are already 50 call results in the queue, remove the oldest one
  //   if (_callSuccess.Count == MaxData)
  //     _callSuccess.Dequeue();

  //   // Add the new call result to the queue
  //   _callSuccess.Enqueue(success);
  // }

  // // Method to calculate the success rate
  // public double SuccessRate()
  // {
  //   // If there are no call results, return 0.0
  //   if (_callSuccess.Count == 0)
  //     return 0.0;

  //   // Otherwise, return the success rate
  //   return (double)_callSuccess.Count(x => x) / _callSuccess.Count;
  // }
}