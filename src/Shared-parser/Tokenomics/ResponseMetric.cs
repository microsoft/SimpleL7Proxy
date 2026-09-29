using System.Text.Json.Serialization;

namespace SimpleL7Proxy.Tokenomics;

public readonly struct ResponseMetric
{
    public string UserId { get; }
    public string Model { get; }
    public int DailyInputTokens { get; }
    public int DailyOutputTokens { get; }
    public int DailyCachedTokens { get; }
    public bool IsDailyJailbreakDetected { get; }
    public bool IsDailyContentFiltered { get; }
    public int DailyModel429 { get; }
    public int DailyUser429 { get; }
    public int MonthlyInputTokens { get; }
    public int MonthlyOutputTokens { get; }
    public int MonthlyCachedTokens { get; }
    public bool IsMonthlyJailbreakDetected { get; }
    public bool IsMonthlyContentFiltered { get; }
    public int MonthlyModel429 { get; }
    public int MonthlyUser429 { get; }
    public double DailyAvgLatencyMs { get; }
    public double MonthlyAvgLatencyMs { get; }
    public DateTime ResponseTimeUtc { get; }

    [JsonConstructor]
    public ResponseMetric(
        string userId,
        string model,
        int dailyInputTokens,
        int dailyOutputTokens,
        int dailyCachedTokens,
        bool isDailyJailbreakDetected,
        bool isDailyContentFiltered,
        int dailyModel429,
        int dailyUser429,
        int monthlyInputTokens,
        int monthlyOutputTokens,
        int monthlyCachedTokens,
        bool isMonthlyJailbreakDetected,
        bool isMonthlyContentFiltered,
        int monthlyModel429,
        int monthlyUser429,
        double dailyAvgLatencyMs,
        double monthlyAvgLatencyMs,
        DateTime responseTimeUtc)
    {
        UserId = userId;
        Model = model;
        DailyInputTokens = dailyInputTokens;
        DailyOutputTokens = dailyOutputTokens;
        DailyCachedTokens = dailyCachedTokens;
        IsDailyJailbreakDetected = isDailyJailbreakDetected;
        IsDailyContentFiltered = isDailyContentFiltered;
        DailyModel429 = dailyModel429;
        DailyUser429 = dailyUser429;
        MonthlyInputTokens = monthlyInputTokens;
        MonthlyOutputTokens = monthlyOutputTokens;
        MonthlyCachedTokens = monthlyCachedTokens;
        IsMonthlyJailbreakDetected = isMonthlyJailbreakDetected;
        IsMonthlyContentFiltered = isMonthlyContentFiltered;
        MonthlyModel429 = monthlyModel429;
        MonthlyUser429 = monthlyUser429;
        DailyAvgLatencyMs = dailyAvgLatencyMs;
        MonthlyAvgLatencyMs = monthlyAvgLatencyMs;
        ResponseTimeUtc = responseTimeUtc;
    }

    public string ToString()
    {
        return $"UserId: {UserId}, Model: {Model}, DailyInputTokens: {DailyInputTokens}, DailyOutputTokens: {DailyOutputTokens}, DailyCachedTokens: {DailyCachedTokens}, IsDailyJailbreakDetected: {IsDailyJailbreakDetected}, IsDailyContentFiltered: {IsDailyContentFiltered}, DailyModel429: {DailyModel429}, DailyUser429: {DailyUser429}, MonthlyInputTokens: {MonthlyInputTokens}, MonthlyOutputTokens: {MonthlyOutputTokens}, MonthlyCachedTokens: {MonthlyCachedTokens}, IsMonthlyJailbreakDetected: {IsMonthlyJailbreakDetected}, IsMonthlyContentFiltered: {IsMonthlyContentFiltered}, MonthlyModel429: {MonthlyModel429}, MonthlyUser429: {MonthlyUser429}, DailyAvgLatencyMs: {DailyAvgLatencyMs}, MonthlyAvgLatencyMs: {MonthlyAvgLatencyMs}, ResponseTimeUtc: {ResponseTimeUtc}";
    }
}