namespace Shared.HealthProbe
{
    /// <summary>
    /// Enum representing health status types.
    /// </summary>
    public enum HealthStatusEnum
    {
        LivenessReady = 0,
        ReadinessZeroHosts = 1,
        ReadinessFailedHosts = 2,
        ReadinessReady = 3,
        StartupWorkersNotReady = 4,
        StartupZeroHosts = 5,
        StartupFailedHosts = 6,
        StartupReady = 7
    }
}