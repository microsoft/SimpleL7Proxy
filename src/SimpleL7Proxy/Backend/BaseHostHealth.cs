using OS = System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

public class BackendHostHealth
{
  public Guid guid = Guid.NewGuid();
  public BackendHostConfig HostConfig { get; set; }

  public string Host => HostConfig.Host;
  public string ProbePath => HostConfig.ProbePath;
  public int Port => HostConfig.Port;
  public string Url => HostConfig.Url;
  public string IpAddr => HostConfig.IpAddr ?? Host;
  public string Protocol => HostConfig.Protocol;
  public string ProbeUrl => HostConfig.ProbeUrl;

  private const int MaxData = 50;
  private readonly Queue<double> _latencies = new();
  private readonly Queue<bool> _callSuccess = new();
  public double CalculatedAverageLatency { get; set; }

  private ConcurrentQueue<double> _pxLatency = new ConcurrentQueue<double>();
  private int _errors;

  public BackendHostHealth(
    BackendHostConfig hostConfig,
    ILogger<BackendHostHealth> logger)

  //    public BackendHostHealth(
  //      string hostname, 
  //      string? probepath, 
  //      string? ipaddress)
  {
    HostConfig = hostConfig;

    // Uncomment UNTIL sslStream is implemented
    // if (ipaddress != null)
    // {
    //     // Valudate that the address is in the right format
    //     if (!System.Net.IPAddress.TryParse(ipaddress, out _))
    //     {
    //         throw new System.UriFormatException($"Invalid IP address: {ipaddress}");
    //     }
    //     ipaddr = ipaddress;
    // }

    logger.LogInformation($"[CONFIG] ✓ Backend host registered: {HostConfig.Host} | Probe: {HostConfig.ProbePath}");
  }

  public override string ToString()
  {
    return $"{Protocol}://{Host}:{Port}";
  }

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

      // Reset the error count
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

    // Reset the counts
    _pxLatency = new();// ConcurrentQueue<double>();
    Interlocked.Exchange(ref _errors, 0);


  }

  // Method to add a new latency
  public void AddLatency(double latency)
  {
    // If there are already 50 latencies in the queue, remove the oldest one
    if (_latencies.Count == MaxData)
      _latencies.Dequeue();

    // Add the new latency to the queue
    _latencies.Enqueue(latency);
  }
  // Method to calculate the average latency
  public double AverageLatency()
  {
    // If there are no latencies, return 0.0
    if (_latencies.Count == 0)
      return 0.0;

    // Otherwise, return the average latency + penalty for each failed call
    return _latencies.Average() + (1 - SuccessRate()) * 100;
  }

  // Method to track the success of a call
  public void AddCallSuccess(bool success)
  {
    // If there are already 50 call results in the queue, remove the oldest one
    if (_callSuccess.Count == MaxData)
      _callSuccess.Dequeue();

    // Add the new call result to the queue
    _callSuccess.Enqueue(success);
  }

  // Method to calculate the success rate
  public double SuccessRate()
  {
    // If there are no call results, return 0.0
    if (_callSuccess.Count == 0)
      return 0.0;

    // Otherwise, return the success rate
    return (double)_callSuccess.Count(x => x) / _callSuccess.Count;
  }
}