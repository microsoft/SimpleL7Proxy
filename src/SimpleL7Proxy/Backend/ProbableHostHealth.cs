using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Host health implementation for backends that support health probing.
/// Tracks success rate based on health check results.
/// </summary>
public class ProbeableHostHealth : BaseHostHealth
{
    private const int MaxData = 50;
    private readonly Queue<bool> _callSuccess = new();

    public string ProbePath => Config.ProbePath;
    public string ProbeUrl => Config.ProbeUrl;

    public ProbeableHostHealth(HostConfig hostConfig, ILogger logger)
        : base(hostConfig, logger)
    {
        logger.LogInformation($"[CONFIG] ✓ Probeable backend host: {hostConfig.Host} | Probe: {hostConfig.ProbePath}");
    }

    public override bool SupportsProbing => true;

    public override void AddCallSuccess(bool success)
    {
        // If there are already 50 call results in the queue, remove the oldest one
        if (_callSuccess.Count == MaxData)
            _callSuccess.Dequeue();

        // Add the new call result to the queue
        _callSuccess.Enqueue(success);
    }

    public override double SuccessRate()
    {
        // If there are no call results, return 0.0
        if (_callSuccess.Count == 0)
            return 0.0;

        // Otherwise, return the success rate
        return (double)_callSuccess.Count(x => x) / _callSuccess.Count;
    }
}