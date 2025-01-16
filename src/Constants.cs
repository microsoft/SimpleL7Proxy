public static class Constants
{
    public const string Health = "/health";
    public const string Readiness = "/readiness";
    public const string Startup = "/startup";
    public const string Liveness = "/liveness";

    /// <summary>
    /// An array of probe route constants used for health checks and readiness checks.
    /// </summary>
    public static readonly string[] probes = { Health, Readiness, Startup, Liveness };

}