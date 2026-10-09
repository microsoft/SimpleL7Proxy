using System.Globalization;
using System.Text;

namespace SimpleL7Proxy.Tokenomics;

public readonly struct PendingMetric
{
    public string UserId { get; }
    public string Model { get; }
    public int InputTokens { get; }
    public int OutputTokens { get; }
    public int CachedTokens { get; }
    public bool IsJailbreakDetected { get; }
    public bool IsContentFiltered { get; }
    public DateOnly Day { get; }
    public int? StatusCode { get; }
    public double? LatencyMs { get; }
    public DateTime TimestampUtc { get; }

    public PendingMetric(
        string userId,
        string model,
        int inputTokens,
        int outputTokens,
        int cachedTokens,
        bool isJailbreakDetected,
        bool isContentFiltered,
        DateOnly day,
        int? statusCode,
        double? latencyMs,
        DateTime timestampUtc)
    {
        UserId = userId;
        Model = model;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CachedTokens = cachedTokens;
        IsJailbreakDetected = isJailbreakDetected;
        IsContentFiltered = isContentFiltered;
        Day = day;
        StatusCode = statusCode;
        LatencyMs = latencyMs;
        TimestampUtc = timestampUtc;
    }

    public PendingMetric(string csvLine)
    {
        // parse based on csvHeader
        var lineParts = csvLine.Split(',');
        UserId = lineParts[0];
        Model = lineParts[1];
        Day = DateOnly.ParseExact(lineParts[2], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        InputTokens = int.Parse(lineParts[3]);
        OutputTokens = int.Parse(lineParts[4]);
        CachedTokens = int.Parse(lineParts[5]);
        IsJailbreakDetected = bool.Parse(lineParts[6]);
        IsContentFiltered = bool.Parse(lineParts[7]);
        StatusCode = string.IsNullOrEmpty(lineParts[8]) ? null : int.Parse(lineParts[8]);
        LatencyMs = string.IsNullOrEmpty(lineParts[9]) ? null : double.Parse(lineParts[9], CultureInfo.InvariantCulture);
        TimestampUtc = DateTime.ParseExact(lineParts[10], "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>
    /// Parses one CSV data row from a character span without allocating field strings. Only the
    /// retained <see cref="UserId"/> and <see cref="Model"/> values allocate. Returns false for
    /// malformed rows.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out PendingMetric metric)
    {
        metric = default;

        Span<Range> fields = stackalloc Range[11];
        if (line.Split(fields, ',') != 11)
        {
            return false;
        }

        var userId = line[fields[0]].Trim();
        var model = line[fields[1]].Trim();
        if (userId.IsEmpty || model.IsEmpty)
        {
            return false;
        }

        if (!DateOnly.TryParseExact(line[fields[2]].Trim(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !int.TryParse(line[fields[3]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var inputTokens)
            || !int.TryParse(line[fields[4]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var outputTokens)
            || !int.TryParse(line[fields[5]].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cachedTokens)
            || !bool.TryParse(line[fields[6]].Trim(), out var isJailbreakDetected)
            || !bool.TryParse(line[fields[7]].Trim(), out var isContentFiltered))
        {
            return false;
        }

        var statusSpan = line[fields[8]].Trim();
        int? statusCode = null;
        if (!statusSpan.IsEmpty)
        {
            if (!int.TryParse(statusSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
            {
                return false;
            }

            statusCode = status;
        }

        var latencySpan = line[fields[9]].Trim();
        double? latencyMs = null;
        if (!latencySpan.IsEmpty)
        {
            if (!double.TryParse(latencySpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var latency))
            {
                return false;
            }

            latencyMs = latency;
        }

        if (!DateTime.TryParseExact(line[fields[10]].Trim(), "yyyy-MM-ddTHH:mm:ss.fffZ",
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var timestampUtc))
        {
            return false;
        }

        metric = new PendingMetric(
            userId.ToString(),
            model.ToString(),
            inputTokens,
            outputTokens,
            cachedTokens,
            isJailbreakDetected,
            isContentFiltered,
            day,
            statusCode,
            latencyMs,
            timestampUtc);
        return true;
    }

    public static string CsvHeader => "UserId,Model,Day,InputTokens,OutputTokens,CachedTokens,IsJailbreakDetected,IsContentFiltered,StatusCode,LatencyMs,TimestampUtc";

    public string ToCSV()
    {
        var csv = new StringBuilder();

        csv.Append(UserId).Append(',')
               .Append(Model).Append(',')
               .Append(Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
               .Append(InputTokens).Append(',')
               .Append(OutputTokens).Append(',')
               .Append(CachedTokens).Append(',')
               .Append(IsJailbreakDetected).Append(',')
               .Append(IsContentFiltered).Append(',')
               .Append(StatusCode.HasValue ? StatusCode.Value.ToString() : "").Append(',')
               .Append(LatencyMs.HasValue ? LatencyMs.Value.ToString(CultureInfo.InvariantCulture) : "").Append(',')
               .Append(TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
               .Append('\n');

        return csv.ToString();
    }
}