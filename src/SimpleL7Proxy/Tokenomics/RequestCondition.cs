public class TokenConditions
{
    public bool HighPriority { get; init; }
    public bool CriticalPriority { get; init; }

    public bool PremiumTenant { get; init; }
    public bool EnterpriseTenant { get; init; }

    public bool AdministratorOverride { get; init; }
    public bool ApprovedException { get; init; }

    public bool LargeContextRequest { get; init; }

    public bool CapacityAvailable { get; init; }
    public bool CapacityConstrained { get; init; }

    public bool DailyQuotaExceeded { get; init; }
    public bool MonthlyQuotaExceeded { get; init; }

    public bool MonthlyBudgetExceeded { get; init; }
    public bool DailyBudgetExceeded { get; init; }

    public bool PreferredModelUnavailable { get; init; }
    public bool ModelReplacementAllowed { get; init; }

    public bool QueueDepthHigh { get; init; }

    public bool ComplianceRequired { get; init; }
    public bool AuditInvestigation { get; init; }

    public bool IncidentResponse { get; init; }

    public bool AbuseDetected { get; init; }
}