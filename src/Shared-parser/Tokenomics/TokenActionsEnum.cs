namespace SimpleL7Proxy.Tokenomics;

public enum TokenActionEnum
{
    None,

    // Request admission
    Reject,

    // Scheduling
    Requeue,
    Delay,
    WaitForReset,

    // Priority management
    IncreasePriority,
    DecreasePriority,

    // Model routing
    ChangeModel,
    UpgradeModel,
    DowngradeModel,

    // Token governance
    IncreaseLimit,
    DecreaseLimit,
    Cap,
    Throttle,

    // Administrative
    Bypass
}


public enum TokenDecisionEnum
{
    Allowed,
    AllowedWithChanges,
    Delayed,
    Requeued,
    Rejected
}
public enum TokenConditionEnum
{
    None,

    // Administrative
    AdministratorOverride,
    ApprovedException,

    // Business priority
    HighPriority,
    CriticalPriority,
    EmergencyAccess,
    IncidentResponse,

    // Tenant characteristics
    PremiumTenant,
    EnterpriseTenant,

    // Capacity conditions
    CapacityAvailable,
    CapacityConstrained,
    ReservedCapacity,

    // Quota conditions
    TokenLimitExceeded,
    RequestLimitExceeded,
    RateLimitExceeded,
    DailyQuotaExceeded,
    MonthlyQuotaExceeded,

    // Budget conditions
    BudgetAvailable,
    BudgetExceeded,

    // Request characteristics
    LargeContextRequest,
    LargeDocumentProcessing,
    CompletionProtection,

    // Compliance
    ComplianceRequired,
    AuditInvestigation,

    // Routing optimization
    ModelCapacityExceeded,
    PreferredModelUnavailable,
    AlternateModelAvailable,

    // Fairness / abuse prevention
    AbuseDetected,
    NoisyNeighborDetected,

    // Queue management
    QueueDepthHigh,
    QueueDepthLow,

    // Time-based
    QuotaResetPending
}