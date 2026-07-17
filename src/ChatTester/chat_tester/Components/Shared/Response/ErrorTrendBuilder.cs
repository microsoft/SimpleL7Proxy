using System.Globalization;

namespace chat_tester.Components.Shared;

public static class ErrorTrendBuilder
{
    private const int DefaultBucketCount = 12;
    private const int SubBucketCount = 10;

    public static ErrorTrendBuildResult Build(IReadOnlyList<ChatHistoryEntry> entries, bool errorsOnly, int? windowMinutesOverride, DateTimeOffset now)
    {
        if (entries.Count == 0)
        {
            return new ErrorTrendBuildResult(Array.Empty<ErrorTrendBucket>(), "recent activity", now, now, 60d);
        }

        var bucketCount = windowMinutesOverride.HasValue
            ? ChooseBucketCount(windowMinutesOverride.Value)
            : DefaultBucketCount;

        double intervalMinutes;
        int windowMinutes;
        DateTimeOffset windowStart;

        if (windowMinutesOverride is { } selectedWindow)
        {
            windowMinutes = selectedWindow;
            intervalMinutes = (double)selectedWindow / bucketCount;
            windowStart = now.AddMinutes(-selectedWindow);
        }
        else
        {
            var earliest = errorsOnly
                ? ResolveClusterStart(entries)
                : entries.Min(entry => entry.CreatedAt);
            var spanMinutes = Math.Max(1.0, (now - earliest).TotalMinutes);
            var intervalInt = ChooseBucketIntervalMinutes(spanMinutes / bucketCount);
            intervalMinutes = intervalInt;
            windowMinutes = intervalInt * bucketCount;
            windowStart = now.AddMinutes(-windowMinutes);
        }

        var windowLabel = FormatWindowLabel(windowMinutes);
        var subMinutes = intervalMinutes / SubBucketCount;
        var alignmentSeconds = NormalizeAlignmentSeconds(subMinutes * 60.0);
        var totalMinis = bucketCount * SubBucketCount;

        var danger = new int[totalMinis];
        var warning = new int[totalMinis];
        var info = new int[totalMinis];
        var success = new int[totalMinis];
        var secondary = new int[totalMinis];

        foreach (var entry in entries)
        {
            var offsetMinutes = (entry.CreatedAt - windowStart).TotalMinutes;
            if (offsetMinutes < 0)
            {
                continue;
            }

            var idx = Math.Min((int)(offsetMinutes / subMinutes), totalMinis - 1);
            switch (HistoryEntryFormatter.GetHistoryStatusCode(entry))
            {
                case 429:
                    warning[idx]++;
                    break;
                case >= 500:
                    danger[idx]++;
                    break;
                case >= 400:
                    info[idx]++;
                    break;
                case >= 200 and < 300:
                    success[idx]++;
                    break;
                default:
                    secondary[idx]++;
                    break;
            }
        }

        var buckets = new List<ErrorTrendBucket>(bucketCount);
        for (var slot = 0; slot < bucketCount; slot++)
        {
            var backMinutes = (bucketCount - 1 - slot) * intervalMinutes;
            string label;
            if (slot == bucketCount - 1)
            {
                label = "now";
            }
            else if (intervalMinutes < 1)
            {
                label = $"-{(int)Math.Round(backMinutes * 60)}s";
            }
            else if (intervalMinutes < 60)
            {
                label = $"-{(int)Math.Round(backMinutes)}m";
            }
            else if (intervalMinutes < 1440)
            {
                label = $"-{(int)Math.Round(backMinutes / 60)}h";
            }
            else
            {
                label = $"-{(int)Math.Round(backMinutes / 1440)}d";
            }

            var slotBase = slot * SubBucketCount;
            var slotDanger = 0;
            var slotWarning = 0;
            var slotInfo = 0;
            var slotSuccess = 0;
            var slotSecondary = 0;
            var minis = new List<ErrorTrendMini>(SubBucketCount);

            for (var miniIndex = 0; miniIndex < SubBucketCount; miniIndex++)
            {
                var idx = slotBase + miniIndex;
                slotDanger += danger[idx];
                slotWarning += warning[idx];
                slotInfo += info[idx];
                slotSuccess += success[idx];
                slotSecondary += secondary[idx];
                minis.Add(new ErrorTrendMini(danger[idx], warning[idx], info[idx], success[idx], secondary[idx]));
            }

            buckets.Add(new ErrorTrendBucket(label, slotDanger, slotWarning, slotInfo, slotSuccess, slotSecondary, minis));
        }

        return new ErrorTrendBuildResult(buckets, windowLabel, windowStart, now, alignmentSeconds);
    }

    private static DateTimeOffset ResolveClusterStart(IReadOnlyList<ChatHistoryEntry> entries)
    {
        var ordered = entries.Select(entry => entry.CreatedAt).OrderByDescending(time => time).ToList();
        var newest = ordered[0];
        var clusterOldest = ordered[0];
        for (var i = 1; i < ordered.Count; i++)
        {
            var gapMinutes = (clusterOldest - ordered[i]).TotalMinutes;
            var clusterSpanMinutes = (newest - clusterOldest).TotalMinutes;
            var maxGapMinutes = Math.Max(30.0, clusterSpanMinutes * 4.0);
            if (gapMinutes > maxGapMinutes)
            {
                break;
            }

            clusterOldest = ordered[i];
        }

        return clusterOldest;
    }

    private static int ChooseBucketCount(int windowMinutes) => windowMinutes switch
    {
        <= 5 => 12,
        <= 10 => 12,
        <= 30 => 12,
        <= 60 => 12,
        <= 360 => 12,
        <= 1440 => 24,
        <= 10080 => 7,
        _ => 30
    };

    private static int ChooseBucketIntervalMinutes(double targetMinutes)
    {
        var niceIntervals = new[] { 1, 2, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440, 2880, 10080 };
        foreach (var interval in niceIntervals)
        {
            if (interval >= targetMinutes)
            {
                return interval;
            }
        }

        return niceIntervals[^1];
    }

    private static string FormatWindowLabel(int windowMinutes)
    {
        if (windowMinutes >= 43200)
        {
            return $"the last {windowMinutes / 43200.0:0.#} mo";
        }

        if (windowMinutes >= 1440)
        {
            return $"the last {windowMinutes / 1440.0:0.#} d";
        }

        if (windowMinutes >= 60)
        {
            return $"the last {windowMinutes / 60.0:0.#} h";
        }

        return $"the last {windowMinutes} min";
    }

    private static double NormalizeAlignmentSeconds(double rawSeconds)
    {
        var niceSteps = new[]
        {
            2d, 5d, 10d, 15d, 30d,
            60d, 120d, 300d, 600d, 900d, 1800d,
            3600d, 7200d, 14400d, 21600d, 43200d,
            86400d
        };

        foreach (var step in niceSteps)
        {
            if (step >= rawSeconds)
            {
                return step;
            }
        }

        return 86400d;
    }
}
