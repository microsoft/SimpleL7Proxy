namespace SimpleL7Proxy;
public static class Constants
{
    public const string Health = "/health";
    public const string Readiness = "/readiness";
    public const string Startup = "/startup";
    public const string Liveness = "/liveness";
    public const string Shutdown = "/shutdown"; // Signal to unwedge workers and shut down gracefully
    public const string ForceGC = "/forcegc"; // Signal to force garbage collection
    public const string Latency = "latency";
    public const string RoundRobin = "roundrobin";
    public const string Random = "random";
    public const string Server = "simplel7proxy";
    public const string VERSION = "2.3.8.dnet10.3";

    public const int AnyPriority = -1;

    /// <summary>
    /// An array of probe route constants used for health checks and readiness checks.
    /// </summary>
    public static readonly string[] probes = { Health, Readiness, Startup, Liveness, ForceGC };

}