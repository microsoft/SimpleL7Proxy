namespace chat_tester.Components.Shared;

public static class VisibleWindowCalculator
{
    public static (double LeftPercent, double WidthPercent) Calculate(
        int entryCount,
        double scrollTop,
        double clientHeight,
        double scrollHeight,
        IReadOnlyList<ChatHistoryEntry> recentEntries,
        DateTimeOffset trendWindowStart,
        DateTimeOffset trendWindowEnd,
        bool newestFirst)
    {
        if (entryCount <= 1)
        {
            return (0, 100);
        }

        var maxScroll = Math.Max(1.0, scrollHeight - clientHeight);
        var topRatio = Math.Clamp(scrollTop / maxScroll, 0, 1);
        var bottomRatio = Math.Clamp((scrollTop + clientHeight) / Math.Max(1.0, scrollHeight), 0, 1);

        var newestIndex = Math.Clamp((int)Math.Floor(topRatio * (entryCount - 1)), 0, entryCount - 1);
        var oldestIndex = Math.Clamp((int)Math.Ceiling(bottomRatio * (entryCount - 1)), newestIndex, entryCount - 1);

        var newestVisible = recentEntries[newestIndex].CreatedAt;
        var oldestVisible = recentEntries[oldestIndex].CreatedAt;
        var span = (trendWindowEnd - trendWindowStart).TotalMilliseconds;
        if (span <= 0)
        {
            return (0, 100);
        }

        var left = Math.Clamp((oldestVisible - trendWindowStart).TotalMilliseconds / span, 0, 1);
        var right = Math.Clamp((newestVisible - trendWindowStart).TotalMilliseconds / span, 0, 1);

        var baseLeft = Math.Min(left, right) * 100;
        var baseWidth = Math.Max(0.8, Math.Abs(right - left) * 100);

        return newestFirst
            ? (100.0 - (baseLeft + baseWidth), baseWidth)
            : (baseLeft, baseWidth);
    }
}
