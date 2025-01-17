using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

public class BackendHostHealth
{
  public BackendHostConfig HostConfig { get; set; }

  public string Host => HostConfig.Host;
  public string ProbePath => HostConfig.ProbePath;
  public int Port => HostConfig.Port;
  public string Url => HostConfig.Url;
  public string ProbeUrl => HostConfig.ProbeUrl;

  private const int MaxData = 50;
  private readonly Queue<double> latencies = new();
  private readonly Queue<bool> callSuccess = new();
  public double CalculatedAverageLatency { get; set; }

  private Queue<double> PxLatency = new();
  private int errors = 0;
  private readonly Lock lockObj = new();

  public BackendHostHealth(BackendHostConfig hostConfig, ILogger<BackendHostHealth> logger)
  {
    HostConfig = hostConfig;
    logger.LogInformation($"Adding backend host: {HostConfig.Host}  probe path: {HostConfig.ProbePath}");
  }
  public override string ToString() => $"{HostConfig.Protocol}://{HostConfig.Host}:{HostConfig.Port}";

  public void AddPxLatency(double latency)
  {
    lock (lockObj)
    {
      PxLatency.Enqueue(latency);
    }
  }

  public void AddError()
  {
    lock (lockObj)
    {
      errors++;
    }
  }

  public string GetStatus(out int calls, out int errorCalls, out double average)
  {
    if (PxLatency.Count == 0)
    {
      errorCalls = errors;
      average = 0;
      calls = 0;

      // Reset the error count
      errors = 0;

      return " - ";
    }

    var status = PxLatency;
    errorCalls = errors;
    lock (lockObj)
    {
      // Reset the counts
      PxLatency = new();
      errors = 0;
    }

    average = Math.Round(status.Average(), 3);
    calls = status.Count;

    return $" Calls: {status.Count} Err: {errorCalls} Avg: {Math.Round(status.Average(), 3)}ms";
  }

  // Method to add a new latency
  public void AddLatency(double latency)
  {
    // If there are already 50 latencies in the queue, remove the oldest one
    if (latencies.Count == MaxData)
      latencies.Dequeue();

    // Add the new latency to the queue
    latencies.Enqueue(latency);
  }
  // Method to calculate the average latency
  public double AverageLatency()
  {
    // If there are no latencies, return 0.0
    if (latencies.Count == 0)
      return 0.0;

    // Otherwise, return the average latency + penalty for each failed call
    return latencies.Average() + (1 - SuccessRate()) * 100;
  }

  // Method to track the success of a call
  public void AddCallSuccess(bool success)
  {
    // If there are already 50 call results in the queue, remove the oldest one
    if (callSuccess.Count == MaxData)
      callSuccess.Dequeue();

    // Add the new call result to the queue
    callSuccess.Enqueue(success);
  }

  // Method to calculate the success rate
  public double SuccessRate()
  {
    // If there are no call results, return 0.0
    if (callSuccess.Count == 0)
      return 0.0;

    // Otherwise, return the success rate
    return (double)callSuccess.Count(x => x) / callSuccess.Count;
  }
}