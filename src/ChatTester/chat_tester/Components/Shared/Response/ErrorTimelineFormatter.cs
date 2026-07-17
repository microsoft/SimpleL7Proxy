using System.Globalization;

namespace chat_tester.Components.Shared;

public static class ErrorTimelineFormatter
{
    public static string CompactStatusText(string? statusMessage, string? fallbackStatus)
    {
        var candidate = string.IsNullOrWhiteSpace(statusMessage) ? fallbackStatus : statusMessage;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "Failed";
        }

        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("Completed with ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[15..].Trim();
        }

        var completedWithIndex = trimmed.IndexOf(" completed with ", StringComparison.OrdinalIgnoreCase);
        if (completedWithIndex >= 0)
        {
            return trimmed[(completedWithIndex + 16)..].Trim();
        }

        return trimmed;
    }

    public static string FormatEntrySummary(ChatHistoryEntry entry)
    {
        var normalized = CompactStatusText(entry.StatusMessage, entry.Metrics.Status);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 1 && int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return string.Join(' ', tokens.Skip(1));
        }

        return normalized;
    }

    public static string FormatAlignedTime(DateTimeOffset value, double alignmentSeconds)
    {
        var snapped = SnapToAlignment(value, alignmentSeconds).ToLocalTime();
        return alignmentSeconds >= 86400
            ? snapped.ToString("MM/dd", CultureInfo.InvariantCulture)
            : snapped.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatAlignedAge(DateTimeOffset value, double alignmentSeconds, DateTimeOffset now)
    {
        var snappedValue = SnapToAlignment(value, alignmentSeconds);
        var snappedNow = SnapToAlignment(now, alignmentSeconds);
        var age = snappedNow - snappedValue;
        if (alignmentSeconds < 60)
        {
            var seconds = Math.Max(0, (int)Math.Round(age.TotalSeconds));
            return seconds == 0 ? "now" : $"{seconds}s ago";
        }

        if (alignmentSeconds < 3600)
        {
            return $"{Math.Max(0, (int)Math.Round(age.TotalMinutes))}m ago";
        }

        if (alignmentSeconds < 86400)
        {
            return $"{Math.Max(0, (int)Math.Round(age.TotalHours))}h ago";
        }

        return $"{Math.Max(0, (int)Math.Round(age.TotalDays))}d ago";
    }

    private static DateTimeOffset SnapToAlignment(DateTimeOffset value, double alignmentSeconds)
    {
        var ticks = TimeSpan.FromSeconds(alignmentSeconds).Ticks;
        if (ticks <= 0)
        {
            return value;
        }

        var snappedTicks = value.UtcTicks - (value.UtcTicks % ticks);
        return new DateTimeOffset(snappedTicks, TimeSpan.Zero).ToOffset(value.Offset);
    }
}
