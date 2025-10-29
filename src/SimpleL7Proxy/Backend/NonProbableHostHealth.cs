using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Host health implementation for backends that do not support health probing.
/// Always considered healthy and available.
/// </summary>
public class NonProbeableHostHealth : BaseHostHealth
{
    public NonProbeableHostHealth(HostConfig hostConfig, ILogger logger)
        : base(hostConfig, logger)
    {
        logger.LogInformation($"[CONFIG] ✓ Non-probeable backend host: {HostConfig.Host} (always active)");
    }

    public override bool SupportsProbing => false;

    /// <summary>
    /// Non-probeable hosts are always considered successful.
    /// </summary>
    public override double SuccessRate()
    {
        return 1.0; // Always 100% success rate
    }

    /// <summary>
    /// For non-probeable hosts, this method is a no-op since we don't track health checks.
    /// Success tracking would be based on actual request results, not probes.
    /// </summary>
    public override void AddCallSuccess(bool success)
    {
        // No-op for non-probeable hosts
        // Could optionally track operational success for monitoring purposes
    }

    /// <summary>
    /// Override average latency calculation to remove the success rate penalty
    /// since non-probeable hosts don't have health check failures.
    /// </summary>
    public override double AverageLatency()
    {
        // For non-probeable hosts, just return the raw average without success rate penalty
        var latencies = GetLatencies();
        return latencies.Count == 0 ? 0.0 : latencies.Average();
    }
}