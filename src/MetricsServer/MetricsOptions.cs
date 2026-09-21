using System.Globalization;

namespace MetricsServer;

/// <summary>
/// Runtime configuration for the metrics server, sourced from environment variables.
/// All values are read once at startup; the server keeps data in memory only.
/// </summary>
public sealed class MetricsOptions
{
    /// <summary>TCP port the server listens on.</summary>
    public int Port { get; init; } = Constants.DefaultPort;

    /// <summary>Width of a single rollup bucket, in seconds.</summary>
    public int BucketSeconds { get; init; } = Constants.DefaultBucketSeconds;

    /// <summary>Number of buckets retained per series (ring buffer depth).</summary>
    public int BucketCount { get; init; } = Constants.DefaultBucketCount;

    /// <summary>Maximum number of distinct user/model series held in memory.</summary>
    public int MaxSeries { get; init; } = Constants.DefaultMaxSeries;

    /// <summary>Maximum accepted request body size, in bytes.</summary>
    public int MaxRequestBodyBytes { get; init; } = Constants.DefaultMaxRequestBodyBytes;

    /// <summary>Total retention window, in seconds.</summary>
    public int RetentionSeconds => BucketSeconds * BucketCount;

    /// <summary>
    /// Builds the options from environment variables, falling back to defaults when a value
    /// is missing or cannot be parsed.
    /// </summary>
    public static MetricsOptions FromEnvironment()
    {
        return new MetricsOptions
        {
            Port = ReadInt(Constants.PortEnvironmentVariable, Constants.DefaultPort, 1, 65535),
            BucketSeconds = ReadInt(Constants.BucketSecondsEnvironmentVariable, Constants.DefaultBucketSeconds, 1, 3600),
            BucketCount = ReadInt(Constants.BucketCountEnvironmentVariable, Constants.DefaultBucketCount, 1, 10_000),
            MaxSeries = ReadInt(Constants.MaxSeriesEnvironmentVariable, Constants.DefaultMaxSeries, 1, 10_000_000),
            MaxRequestBodyBytes = ReadInt(
                Constants.MaxRequestBodyBytesEnvironmentVariable,
                Constants.DefaultMaxRequestBodyBytes,
                1024,
                64 * 1024 * 1024)
        };
    }

    private static int ReadInt(string name, int defaultValue, int minValue, int maxValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        if (value < minValue || value > maxValue)
        {
            return defaultValue;
        }

        return value;
    }
}
