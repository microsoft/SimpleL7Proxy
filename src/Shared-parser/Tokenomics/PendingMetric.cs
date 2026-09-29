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