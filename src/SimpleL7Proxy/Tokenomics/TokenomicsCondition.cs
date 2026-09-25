namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;

public sealed class TokenomicsCondition
{
    public bool AbuseDetected { get; set; }
    public bool MonthlyQuotaExceeded { get; set; }
    public bool AdministratorOverride { get; set; }
    public bool ApprovedException { get; set; }
    public bool DailyQuotaExceeded { get; set; }
    public bool IncidentResponse { get; set; }
    public bool AuditInvestigation { get; set; }
    public bool ComplianceRequired { get; set; }
    public bool MonthlyBudgetExceeded { get; set; }
    public bool PremiumTenant { get; set; }
    public bool EnterpriseTenant { get; set; }
    public bool DailyBudgetExceeded { get; set; }
    public bool CapacityConstrained { get; set; }
    public bool CriticalPriority { get; set; }
    public bool HighPriority { get; set; }
    public bool QueueDepthHigh { get; set; }
    public bool PreferredModelUnavailable { get; set; }
    public bool ModelReplacementAllowed { get; set; }
    public bool LargeContextRequest { get; set; }
    public bool CapacityAvailable { get; set; }

    public TokenomicsCondition(
        RequestData data,
        TokenomicsSettings settings,
        TokenMetricsCache tokenMetricsCache,
        IConcurrentPriQueue<RequestData> queue)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tokenMetricsCache);
        ArgumentNullException.ThrowIfNull(queue);

        AbuseDetected = data.IsAbusive;

        DailyQuotaExceeded = tokenMetricsCache.GetDailyTokenBalance(data.UserID, data.Model) >= settings.DailyTokenLimit;
        MonthlyQuotaExceeded = tokenMetricsCache.GetMonthlyTokenBalance(data.UserID, data.Model) >= settings.MonthlyTokenLimit;

        MonthlyBudgetExceeded = tokenMetricsCache.GetMonthlyBudgetUsage(data.UserID, data.Model) >= settings.MonthlyBudgetUsd;
        DailyBudgetExceeded = tokenMetricsCache.GetDailyBudgetUsage(data.UserID, data.Model) >= settings.DailyBudgetUsd;

        CapacityConstrained = settings.CurrentCapacityUtilizationPercent >= settings.CapacityConstraintThresholdPercent;
        CapacityAvailable = settings.CurrentCapacityUtilizationPercent < settings.CapacityConstraintThresholdPercent;

        QueueDepthHigh = queue.thrdSafeCount >= (queue.MaxQueueLength * settings.HighQueueDepthThreshold);

        // PreferredModelUnavailable = !tokenMetricsCache.IsModelAvailable(data.Model);
        ModelReplacementAllowed = data.ModelReplacementAllowed;

        LargeContextRequest = data.S7PInputTokens >= settings.LargeContextThreshold;

        CriticalPriority = data.Priority <= settings.CriticalPriority;
        HighPriority = data.Priority <= settings.HighPriority;

        AdministratorOverride = bool.TryParse(data.Headers[Constants.TokenizerAdministratorOverride], out var administratorOverride) && administratorOverride;
        ApprovedException = bool.TryParse(data.Headers[Constants.TokenizerApprovedException], out var approvedException) && approvedException;

        PremiumTenant = bool.TryParse(data.Headers[Constants.TokenizerPremiumTenant], out var premiumTenant) && premiumTenant;
        EnterpriseTenant = bool.TryParse(data.Headers[Constants.TokenizerEnterpriseTenant], out var enterpriseTenant) && enterpriseTenant;

        IncidentResponse = bool.TryParse(data.Headers[Constants.TokenizerIncidentResponse], out var incidentResponse) && incidentResponse;
        AuditInvestigation = bool.TryParse(data.Headers[Constants.TokenizerAuditInvestigation], out var auditInvestigation) && auditInvestigation;
        ComplianceRequired = bool.TryParse(data.Headers[Constants.TokenizerComplianceRequired], out var complianceRequired) && complianceRequired;
    }
}