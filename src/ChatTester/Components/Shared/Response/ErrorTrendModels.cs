namespace chat_tester.Components.Shared;

public sealed record ErrorTrendSegment(string Css, int Count, int Percent);

public sealed record ErrorTrendMini(int Danger, int Warning, int Info, int Success, int Secondary)
{
    public int Count => Danger + Warning + Info + Success + Secondary;

    public IReadOnlyList<ErrorTrendSegment> Segments
    {
        get
        {
            var total = Count;
            if (total == 0)
            {
                return Array.Empty<ErrorTrendSegment>();
            }

            return new[]
                {
                    ("sev-danger", Danger),
                    ("sev-warning", Warning),
                    ("sev-info", Info),
                    ("sev-success", Success),
                    ("sev-secondary", Secondary)
                }
                .Where(category => category.Item2 > 0)
                .Select(category => new ErrorTrendSegment(category.Item1, category.Item2, (int)Math.Round(category.Item2 * 100.0 / total)))
                .ToList();
        }
    }
}

public sealed record ErrorTrendBucket(string Label, int Danger, int Warning, int Info, int Success, int Secondary, IReadOnlyList<ErrorTrendMini> Minis)
{
    public int Count => Danger + Warning + Info + Success + Secondary;

    public int MaxMiniCount => Minis.Count == 0 ? 0 : Minis.Max(mini => mini.Count);

    public string Tooltip
    {
        get
        {
            if (Count == 0)
            {
                return $"{Label}: no activity";
            }

            var parts = new List<string>();
            if (Danger > 0)
            {
                parts.Add($"{Danger} 5xx");
            }

            if (Warning > 0)
            {
                parts.Add($"{Warning} 429");
            }

            if (Info > 0)
            {
                parts.Add($"{Info} 4xx");
            }

            if (Success > 0)
            {
                parts.Add($"{Success} 2xx");
            }

            if (Secondary > 0)
            {
                parts.Add($"{Secondary} other");
            }

            return $"{Label}: {string.Join(", ", parts)}";
        }
    }
}

public sealed record ErrorTrendBuildResult(
    IReadOnlyList<ErrorTrendBucket> Buckets,
    string WindowLabel,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    double AlignmentSeconds);
