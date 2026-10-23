using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsLimitActions
{
    /// <summary>Gets or sets the hourly-quota action for other workloads.</summary>
    public TokenActionEnum HourlyLimitAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the daily-quota action for other workloads.</summary>
    public TokenActionEnum DailyLimitAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the monthly-quota action for other workloads.</summary>
    public TokenActionEnum MonthlyLimitAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the hourly-budget action for other tenants.</summary>
    public TokenActionEnum HourlyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;
    /// <summary>Gets or sets the daily-budget action for other tenants.</summary>
    public TokenActionEnum DailyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;
    /// <summary>Gets or sets the monthly-budget action for other tenants.</summary>
    public TokenActionEnum MonthlyBudgetExceededAction { get; set; } = TokenActionEnum.DowngradeModel;

}
