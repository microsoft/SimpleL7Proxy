namespace MetricsServer;

/// <summary>
/// Route and configuration constants for the Metrics Server.
/// </summary>
public static class Constants
{
    public const string VERSION = "1.0.0";

    // Probe routes
    public const string Health = "/health";
    public const string Liveness = "/liveness";
    public const string Readiness = "/readiness";

    // Ingest routes
    public const string Rollup = "/metrics/rollup";

    // Query routes
    public const string Status = "/metrics/status";
    public const string Users = "/metrics/users";
    public const string Models = "/metrics/models";
    public const string Series = "/metrics/series";
    public const string Stats = "/metrics/stats";

    // Environment variable names
    public const string PortEnvironmentVariable = "METRICSSERVER_PORT";
    public const string BucketSecondsEnvironmentVariable = "METRICSSERVER_BUCKET_SECONDS";
    public const string BucketCountEnvironmentVariable = "METRICSSERVER_BUCKET_COUNT";
    public const string MaxSeriesEnvironmentVariable = "METRICSSERVER_MAX_SERIES";
    public const string MaxRequestBodyBytesEnvironmentVariable = "METRICSSERVER_MAX_BODY_BYTES";

    // Defaults
    public const int DefaultPort = 9100;
    public const int DefaultBucketSeconds = 60;
    public const int DefaultBucketCount = 60;
    public const int DefaultMaxSeries = 100_000;
    public const int DefaultMaxRequestBodyBytes = 4 * 1024 * 1024;
}
