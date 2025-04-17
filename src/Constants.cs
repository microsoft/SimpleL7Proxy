public static class Constants
{
    public const string Health = "/health";
    public const string Readiness = "/readiness";
    public const string Startup = "/startup";
    public const string Liveness = "/liveness";
    public const string Shutdown = "/shutdown"; // Signal to unwedge workers and shut down gracefully
    public const string VERSION = "2.1.20";

    public const int AnyPriority = -1;

    /// <summary>
    /// An array of probe route constants used for health checks and readiness checks.
    /// </summary>
    public static readonly string[] probes = { Health, Readiness, Startup, Liveness };

}
