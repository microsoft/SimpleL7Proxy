namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;

public sealed class TokenomicsCondition
{
    public bool AbuseDetected { get; set; }

    // Standard work
    public bool HourlyQuotaExceeded { get; set; }
    public bool DailyQuotaExceeded { get; set; }
    public bool MonthlyQuotaExceeded { get; set; }
    public bool HourlyBudgetExceeded { get; set; }
    public bool DailyBudgetExceeded { get; set; }
    public bool MonthlyBudgetExceeded { get; set; }

    // Premium Workload
    public bool PremiumHourlyQuotaExceeded { get; set; }
    public bool PremiumDailyQuotaExceeded { get; set; }
    public bool PremiumMonthlyQuotaExceeded { get; set; }
    public bool PremiumHourlyBudgetExceeded { get; set; }
    public bool PremiumDailyBudgetExceeded { get; set; }
    public bool PremiumMonthlyBudgetExceeded { get; set; }
    public bool PremiumTenant { get; set; }
    public bool EnterpriseTenant { get; set; }

    // Critical Workload
    public bool IncidentResponse { get; set; }
    public bool AuditInvestigation { get; set; }
    public bool ComplianceRequired { get; set; }
   
    public bool AdministratorOverride { get; set; }
    public bool ApprovedException { get; set; }
    public bool CapacityConstrained { get; set; }
    public bool CriticalPriority { get; set; }
    public bool HighPriority { get; set; }
    public bool QueueDepthHigh { get; set; }
    public bool PreferredModelUnavailable { get; set; }
    public bool ModelReplacementAllowed { get; set; }
    public bool LargeContextRequest { get; set; }
    public bool CapacityAvailable { get; set; }
    public ResponseMetric Metric { get; set; }

    public async Task<TokenomicsCondition> CreateAsync(
        RequestData data,
        TokenomicsSettings settings,
        LiveMetrics lm,
        IConcurrentPriQueue<RequestData> queue)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(lm);
        ArgumentNullException.ThrowIfNull(queue);

        AbuseDetected = data.IsAbusive;
        Metric = await lm.GetMetric(data.UserID, data.Model);

        // lookup the appropriate limit:  Critical => Premium => Model-specific => Default
        var limits = data.IsCriticalWorkload ? settings.PrivilegedLimitsAndBudgets
            : data.IsPremiumWorkload ? settings.PremiumLimitsAndBudgets
            : data.Model is not null && settings.ModelLimits.TryGetValue(data.Model, out var modelLimits)
                ? modelLimits : settings.DefaultLimitsAndBudgets;

        HourlyQuotaExceeded = lm.GetHourlyTokenBalanceAsync(Metric) >= limits.HourlyTokenLimit;
        DailyQuotaExceeded = lm.GetDailyTokenBalanceAsync(Metric) >= limits.DailyTokenLimit;
        MonthlyQuotaExceeded = lm.GetMonthlyTokenBalanceAsync(Metric) >= limits.MonthlyTokenLimit;

        //MonthlyBudgetExceeded = await lm.GetMonthlyBudgetUsageAsync(data.UserID, data.Model) >= settings.MonthlyBudgetUsd;
        //DailyBudgetExceeded = await lm.GetDailyBudgetUsageAsync(data.UserID, data.Model) >= settings.DailyBudgetUsd;

        CapacityConstrained = settings.CurrentCapacityUtilizationPercent >= settings.CapacityConstraintThresholdPercent;
        CapacityAvailable = settings.CurrentCapacityUtilizationPercent < settings.CapacityConstraintThresholdPercent;

        QueueDepthHigh = queue.thrdSafeCount >= (queue.MaxQueueLength * settings.HighQueueDepthThreshold);

        // PreferredModelUnavailable = !lm.IsModelAvailable(data.Model);
        ModelReplacementAllowed = data.ModelReplacementAllowed;

        LargeContextRequest = data.S7PInputTokens >= settings.LargeContextThreshold;

        CriticalPriority = data.Priority <= settings.CriticalPriority;
        HighPriority = data.Priority <= settings.HighPriority;


        //IncidentResponse = bool.TryParse(data.Headers[Constants.TokenizerIncidentResponse], out var incidentResponse) && incidentResponse;
        //AuditInvestigation = bool.TryParse(data.Headers[Constants.TokenizerAuditInvestigation], out var auditInvestigation) && auditInvestigation;
        //ComplianceRequired = bool.TryParse(data.Headers[Constants.TokenizerComplianceRequired], out var complianceRequired) && complianceRequired;

        return this;
    }

    public override string ToString()
    {
        return $"AbuseDetected: {AbuseDetected}, DailyQuotaExceeded: {DailyQuotaExceeded}, MonthlyQuotaExceeded: {MonthlyQuotaExceeded}, " +
               $"CapacityConstrained: {CapacityConstrained}, CapacityAvailable: {CapacityAvailable}, QueueDepthHigh: {QueueDepthHigh}, " +
               $"ModelReplacementAllowed: {ModelReplacementAllowed}, LargeContextRequest: {LargeContextRequest}, " +
               $"CriticalPriority: {CriticalPriority}, HighPriority: {HighPriority}, " +
               $"AdministratorOverride: {AdministratorOverride}, ApprovedException: {ApprovedException}, " +
               $"PremiumTenant: {PremiumTenant}, EnterpriseTenant: {EnterpriseTenant}, " +
               $"IncidentResponse: {IncidentResponse}, AuditInvestigation: {AuditInvestigation}, ComplianceRequired: {ComplianceRequired}";
    }
}