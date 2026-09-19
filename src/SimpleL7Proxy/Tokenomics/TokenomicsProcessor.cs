namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;

public class TokenomicsProcessor
{
    public IConcurrentPriQueue<RequestData> Queue { get; }
    public TokenMetricsCache TokenMetricsCache { get; }
public TokenomicsProcessor( IConcurrentPriQueue<RequestData> queue, TokenMetricsCache tokenMetricsCache)
    {
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));    
        TokenMetricsCache = tokenMetricsCache ?? throw new ArgumentNullException(nameof(tokenMetricsCache));
    }

    public TokenActionEnum Evaluate(RequestData data, TokenomicsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(settings);

        TokenConditions c = new TokenConditions
        {
            // Hard stop signals
            AbuseDetected = data.IsAbusive,

            // Quotas
            DailyQuotaExceeded = TokenMetricsCache.GetDailyTokenBalance(data.UserID) >= settings.DailyTokenLimit,
            MonthlyQuotaExceeded = TokenMetricsCache.GetMonthlyTokenBalance(data.UserID) >= settings.MonthlyTokenLimit,

            // Budget
            MonthlyBudgetExceeded = TokenMetricsCache.GetMonthlyBudgetUsage(data.UserID) >= settings.MonthlyBudgetUsd,
            DailyBudgetExceeded = TokenMetricsCache.GetDailyBudgetUsage(data.UserID) >= settings.DailyBudgetUsd,

            // Capacity
            CapacityConstrained =               
            (settings.CurrentCapacityUtilizationPercent >= settings.CapacityConstraintThresholdPercent),

            CapacityAvailable =
                settings.CurrentCapacityUtilizationPercent <
                settings.CapacityConstraintThresholdPercent,

            // Queue pressure
            QueueDepthHigh = Queue.thrdSafeCount >= (Queue.MaxQueueLength * settings.HighQueueDepthThreshold),

            // Routing
            PreferredModelUnavailable = !TokenMetricsCache.IsModelAvailable(data.Model),
            ModelReplacementAllowed = data.ModelReplacementAllowed,

            // Context sizing
            LargeContextRequest = data.S7PInputTokens >= settings.LargeContextThreshold,

            // Priority
            CriticalPriority = data.Priority <= settings.CriticalPriority,
            HighPriority =     data.Priority <= settings.HighPriority,

            AdministratorOverride = bool.TryParse(data.Headers[Constants.TokenizerAdministratorOverride], out var administratorOverride) && administratorOverride,
            ApprovedException = bool.TryParse(data.Headers[Constants.TokenizerApprovedException], out var approvedException) && approvedException,

            // Tenant entitlements
            PremiumTenant = bool.TryParse(data.Headers[Constants.TokenizerPremiumTenant], out var premiumTenant) && premiumTenant,
            EnterpriseTenant = bool.TryParse(data.Headers[Constants.TokenizerEnterpriseTenant], out var enterpriseTenant) && enterpriseTenant,

            // Governance workloads
            IncidentResponse = bool.TryParse(data.Headers[Constants.TokenizerIncidentResponse], out var incidentResponse) && incidentResponse,
            AuditInvestigation = bool.TryParse(data.Headers[Constants.TokenizerAuditInvestigation], out var auditInvestigation) && auditInvestigation,
            ComplianceRequired = bool.TryParse(data.Headers[Constants.TokenizerComplianceRequired], out var complianceRequired) && complianceRequired
        };

        return new DecisionTree(settings).Evaluate(c);
    }
}
