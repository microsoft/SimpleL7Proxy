using System.Text.Json.Serialization;

namespace SimpleL7Proxy.Tokenomics;

public readonly struct ResponseMetric
{
    public string UserId { get; }
    public string Model { get; }
    public long HourlyInputTokens { get; }
    public long HourlyOutputTokens { get; }
    public long HourlyCachedTokens { get; }
    public bool IsHourlyJailbreakDetected { get; }
    public bool IsHourlyContentFiltered { get; }
    public int HourlyModel429 { get; }
    public int HourlyUser429 { get; }
    public double HourlyAvgLatencyMs { get; }
    /// <summary>Consumed USD for the requested user/model pair in the hour.</summary>
    public decimal HourlyUserBudget { get; }
    /// <summary>Consumed USD for the requested user/model pair in the day.</summary>
    public decimal DailyUserBudget { get; }
    /// <summary>Consumed USD for the requested user/model pair in the month.</summary>
    public decimal MonthlyUserBudget { get; }
    /// <summary>Consumed USD across all users of the requested model in the hour.</summary>
    public decimal HourlyModelBudget { get; }
    /// <summary>Consumed USD across all users of the requested model in the day.</summary>
    public decimal DailyModelBudget { get; }
    /// <summary>Consumed USD across all users of the requested model in the month.</summary>
    public decimal MonthlyModelBudget { get; }
    public long DailyInputTokens { get; }
    public long DailyOutputTokens { get; }
    public long DailyCachedTokens { get; }
    public bool IsDailyJailbreakDetected { get; }
    public bool IsDailyContentFiltered { get; }
    public int DailyModel429 { get; }
    public int DailyUser429 { get; }
    public long MonthlyInputTokens { get; }
    public long MonthlyOutputTokens { get; }
    public long MonthlyCachedTokens { get; }
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
        long dailyInputTokens,
        long dailyOutputTokens,
        long dailyCachedTokens,
        bool isDailyJailbreakDetected,
        bool isDailyContentFiltered,
        int dailyModel429,
        int dailyUser429,
        long monthlyInputTokens,
        long monthlyOutputTokens,
        long monthlyCachedTokens,
        bool isMonthlyJailbreakDetected,
        bool isMonthlyContentFiltered,
        int monthlyModel429,
        int monthlyUser429,
        double dailyAvgLatencyMs,
        double monthlyAvgLatencyMs,
        DateTime responseTimeUtc,
        long hourlyInputTokens = 0,
        long hourlyOutputTokens = 0,
        long hourlyCachedTokens = 0,
        bool isHourlyJailbreakDetected = false,
        bool isHourlyContentFiltered = false,
        int hourlyModel429 = 0,
        int hourlyUser429 = 0,
        double hourlyAvgLatencyMs = 0,
        decimal hourlyUserBudget = 0,
        decimal dailyUserBudget = 0,
        decimal monthlyUserBudget = 0,
        decimal hourlyModelBudget = 0,
        decimal dailyModelBudget = 0,
        decimal monthlyModelBudget = 0)
    {
        UserId = userId;
        Model = model;
        HourlyInputTokens = hourlyInputTokens;
        HourlyOutputTokens = hourlyOutputTokens;
        HourlyCachedTokens = hourlyCachedTokens;
        IsHourlyJailbreakDetected = isHourlyJailbreakDetected;
        IsHourlyContentFiltered = isHourlyContentFiltered;
        HourlyModel429 = hourlyModel429;
        HourlyUser429 = hourlyUser429;
        HourlyAvgLatencyMs = hourlyAvgLatencyMs;
        HourlyUserBudget = hourlyUserBudget;
        DailyUserBudget = dailyUserBudget;
        MonthlyUserBudget = monthlyUserBudget;
        HourlyModelBudget = hourlyModelBudget;
        DailyModelBudget = dailyModelBudget;
        MonthlyModelBudget = monthlyModelBudget;
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

    public override string ToString()
    {
        return $"UserId: {UserId}, Model: {Model}, DailyInputTokens: {DailyInputTokens}, DailyOutputTokens: {DailyOutputTokens}, DailyCachedTokens: {DailyCachedTokens}, IsDailyJailbreakDetected: {IsDailyJailbreakDetected}, IsDailyContentFiltered: {IsDailyContentFiltered}, DailyModel429: {DailyModel429}, DailyUser429: {DailyUser429}, MonthlyInputTokens: {MonthlyInputTokens}, MonthlyOutputTokens: {MonthlyOutputTokens}, MonthlyCachedTokens: {MonthlyCachedTokens}, IsMonthlyJailbreakDetected: {IsMonthlyJailbreakDetected}, IsMonthlyContentFiltered: {IsMonthlyContentFiltered}, MonthlyModel429: {MonthlyModel429}, MonthlyUser429: {MonthlyUser429}, DailyAvgLatencyMs: {DailyAvgLatencyMs}, MonthlyAvgLatencyMs: {MonthlyAvgLatencyMs}, ResponseTimeUtc: {ResponseTimeUtc}, HourlyInputTokens: {HourlyInputTokens}, HourlyOutputTokens: {HourlyOutputTokens}, HourlyCachedTokens: {HourlyCachedTokens}, IsHourlyJailbreakDetected: {IsHourlyJailbreakDetected}, IsHourlyContentFiltered: {IsHourlyContentFiltered}, HourlyModel429: {HourlyModel429}, HourlyUser429: {HourlyUser429}, HourlyAvgLatencyMs: {HourlyAvgLatencyMs}, HourlyUserBudget: {HourlyUserBudget}, DailyUserBudget: {DailyUserBudget}, MonthlyUserBudget: {MonthlyUserBudget}, HourlyModelBudget: {HourlyModelBudget}, DailyModelBudget: {DailyModelBudget}, MonthlyModelBudget: {MonthlyModelBudget}";
    }
}