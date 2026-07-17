namespace chat_tester.Components.Shared.EventHub;

/// <summary>
/// Single source of truth for reading fields out of a parsed Event Hub record. Previously these
/// helpers were duplicated across the reader, the pipeline processors, and the metrics catalog —
/// with a divergent correlation-key precedence that caused subtle grouping bugs.
/// </summary>
internal static class EventFields
{
    /// <summary>Returns the trimmed value for <paramref name="key"/>, or <paramref name="fallback"/> when absent/blank.</summary>
    public static string Get(IReadOnlyDictionary<string, string> data, string key, string fallback = "")
    {
        return data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    /// <summary>Returns the first non-blank value, trimmed, or <c>null</c> if none.</summary>
    public static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Correlation identity shared across a request's lifetime. GUID is stable across enqueue,
    /// every backend attempt, and the final proxy response; MID differs per attempt. Prefer GUID
    /// so lifecycle/grouping correlate, falling back to MID for events that omit GUID.
    /// </summary>
    public static string? CorrelationKey(IReadOnlyDictionary<string, string> data)
    {
        return FirstNonEmpty(Get(data, "GUID"), Get(data, "MID"));
    }
}
