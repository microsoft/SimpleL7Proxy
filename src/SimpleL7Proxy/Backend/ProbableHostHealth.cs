using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Host health implementation for backends that support health probing.
/// Tracks success rate based on health check results.
/// </summary>
public class ProbeableHostHealth : BaseHostHealth
{
    private const int MaxData = 50;
    private readonly bool[] _callResults = new bool[MaxData];
    private int _currentIndex = 0;
    private int _count = 0;
    private bool _hasHadFirstSuccess = false;

    public string ProbePath => Config.ProbePath;
    public string ProbeUrl => Config.ProbeUrl;

    public ProbeableHostHealth(HostConfig hostConfig, ILogger logger)
        : base(hostConfig, logger)
    {
        logger.LogDebug($"[CONFIG] ✓ Probeable backend host: {hostConfig.Host} | Probe: {hostConfig.ProbePath}");
    }

    public override bool SupportsProbing => true;

    public override void AddCallSuccess(bool success)
    {
        // Ignore failures until we've had at least one success (cold start warmup)
        if (!_hasHadFirstSuccess)
        {
            if (success)
            {
                _hasHadFirstSuccess = true;
            }
            else
            {
                // Skip recording failures before first success
                return;
            }
        }

        // Add the new call result to the circular buffer
        _callResults[_currentIndex] = success;
        _currentIndex = (_currentIndex + 1) % MaxData;
        
        // Track count until we fill the buffer for the first time
        if (_count < MaxData)
            _count++;
    }

    public override double SuccessRate()
    {
        // If we haven't had any successful probes yet, return 1.0 to give benefit of doubt during warmup
        if (!_hasHadFirstSuccess || _count == 0)
            return 1.0;

        // Count successful calls in the active portion of the buffer
        int successCount = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_callResults[i])
                successCount++;
        }

        return (double)successCount / _count;
    }
}