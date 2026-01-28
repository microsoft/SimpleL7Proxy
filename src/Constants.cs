public static class Constants
{
    public const string Health = "/health";
    public const string Readiness = "/readiness";
    public const string Startup = "/startup";
    public const string Liveness = "/liveness";
    // public const string Shutdown = "/shutdown"; // Signal to unwedge workers and shut down gracefully
    public const string ForceGC = "/forcegc"; // Signal to force garbage collection

    public const string Latency = "latency";
    public const string RoundRobin = "roundrobin";
    public const string Random = "random";
    public static string REVISION = "rev-2024-06-10";
    public static string CONTAINERAPP = "SimpleL7Proxy";
    public const string VERSION = "2.1.33.P6";

    public const int AnyPriority = -1;

    /// <summary>
    /// An array of probe route constants used for health checks and readiness checks.
    /// </summary>
    public static readonly string[] probes = { Health, Readiness, Startup, Liveness, ForceGC };

}
