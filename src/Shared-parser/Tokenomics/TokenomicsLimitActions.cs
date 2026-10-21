using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsLimitActions
{
    /// <summary>Gets or sets the Hourly-quota action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum HourlyQuotaGovernanceAction { get; set; } = TokenActionEnum.Bypass;
    /// <summary>Gets or sets the daily-quota action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum DailyQuotaGovernanceAction { get; set; } = TokenActionEnum.Bypass;
    /// <summary>Gets or sets the action for an exceeded monthly quota without an override or approved exception.</summary>
    public TokenActionEnum MonthlyQuotaGovernanceAction  { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the Hourly-budget action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum HourlyBudgetGovernanceAction { get; set; } = TokenActionEnum.Bypass;
    /// <summary>Gets or sets the daily-budget action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum DailyBudgetGovernanceAction { get; set; } = TokenActionEnum.Bypass;
    /// <summary>Gets or sets the action for an exceeded monthly budget without an override or approved exception.</summary>
    public TokenActionEnum MonthlyBudgetGovernanceAction  { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the hourly-quota action for other workloads.</summary>
    public TokenActionEnum HourlyQuotaExceededAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the daily-quota action for other workloads.</summary>
    public TokenActionEnum DailyQuotaExceededAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the monthly-quota action for other workloads.</summary>
    public TokenActionEnum MonthlyQuotaExceededAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the hourly-budget action for other tenants.</summary>
    public TokenActionEnum HourlyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;
    /// <summary>Gets or sets the daily-budget action for other tenants.</summary>
    public TokenActionEnum DailyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;
    /// <summary>Gets or sets the monthly-budget action for other tenants.</summary>
    public TokenActionEnum MonthlyBudgetExceededAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the hourly-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum HourlyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the daily-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum DailyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the monthly-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum MonthlyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the hourly-quota action for premium or enterprise tenants.</summary>
    public TokenActionEnum HourlyQuotaEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the daily-quota action for premium or enterprise tenants.</summary>
    public TokenActionEnum DailyQuotaEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;
    /// <summary>Gets or sets the monthly-quota action for premium or enterprise tenants.</summary>
    public TokenActionEnum MonthlyQuotaEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;


}
