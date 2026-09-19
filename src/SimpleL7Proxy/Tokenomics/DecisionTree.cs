namespace SimpleL7Proxy.Tokenomics;

/// <summary>Selects the first matching token-governance action for a request.</summary>
public class DecisionTree {

    private readonly TokenomicsSettings _tokenMetricsSettings;
    /// <summary>Initializes the decision tree with its configured policy actions.</summary>
    public DecisionTree(TokenomicsSettings tokenMetricsSettings) {
        _tokenMetricsSettings = tokenMetricsSettings ?? throw new ArgumentNullException(nameof(tokenMetricsSettings));
    }

    /// <summary>Evaluates request conditions in policy-precedence order.</summary>
    public TokenActionEnum Evaluate(TokenConditions c) {
        // -------------------------------------------------
        // 1. Hard Stop
        // -------------------------------------------------

        if (c.AbuseDetected)
            return _tokenMetricsSettings.AbuseDetectedAction;

        if (c.MonthlyQuotaExceeded &&
            !c.AdministratorOverride &&
            !c.ApprovedException)
            return _tokenMetricsSettings.MonthlyQuotaExceededAction;

        // -------------------------------------------------
        // 2. Administrative Override
        // -------------------------------------------------

        if (c.AdministratorOverride ||
            c.ApprovedException)
            return _tokenMetricsSettings.AdministratorOverrideAction;

        // -------------------------------------------------
        // 3. Quota Handling
        // -------------------------------------------------

        if (c.DailyQuotaExceeded)
        {
            if (c.IncidentResponse ||
                c.AuditInvestigation ||
                c.ComplianceRequired)
                return _tokenMetricsSettings.DailyQuotaGovernanceAction;

            return _tokenMetricsSettings.DailyQuotaExceededAction;
        }

        // -------------------------------------------------
        // 4. Budget Handling
        // -------------------------------------------------

        if (c.MonthlyBudgetExceeded)
        {
            if (c.PremiumTenant ||
                c.EnterpriseTenant)
                return _tokenMetricsSettings.MonthlyBudgetEntitledTenantAction;

            return _tokenMetricsSettings.MonthlyBudgetExceededAction;
        }

        if (c.DailyBudgetExceeded)
        {
            if (c.PremiumTenant ||
                c.EnterpriseTenant)
                return _tokenMetricsSettings.DailyBudgetEntitledTenantAction;

            return _tokenMetricsSettings.DailyBudgetExceededAction;
        }

        // -------------------------------------------------
        // 5. Capacity Handling
        // -------------------------------------------------

        if (c.CapacityConstrained)
        {
            if (c.CriticalPriority ||
                c.IncidentResponse)
                return _tokenMetricsSettings.CapacityCriticalWorkloadAction;

            if (c.HighPriority)
                return _tokenMetricsSettings.CapacityHighPriorityAction;

            return _tokenMetricsSettings.CapacityConstrainedAction;
        }

        // -------------------------------------------------
        // 6. Queue Pressure
        // -------------------------------------------------

        if (c.QueueDepthHigh)
        {
            if (c.CriticalPriority)
                return _tokenMetricsSettings.QueueCriticalPriorityAction;

            return _tokenMetricsSettings.QueueDepthHighAction;
        }

        // -------------------------------------------------
        // 7. Model Routing
        // -------------------------------------------------

        if (c.PreferredModelUnavailable)
        {
            if (c.ModelReplacementAllowed)
                return _tokenMetricsSettings.PreferredModelReplacementAction;

            return _tokenMetricsSettings.PreferredModelUnavailableAction;
        }

        // -------------------------------------------------
        // 8. Large Context Requests
        // -------------------------------------------------

        if (c.LargeContextRequest)
        {
            if (c.CapacityAvailable)
                return _tokenMetricsSettings.LargeContextCapacityAvailableAction;

            return _tokenMetricsSettings.LargeContextRequestAction;
        }

        // -------------------------------------------------
        // 9. Priority Management
        // -------------------------------------------------

        if (c.CriticalPriority)
            return _tokenMetricsSettings.CriticalPriorityAction;

        if (c.HighPriority &&
            (c.PremiumTenant || c.EnterpriseTenant))
            return _tokenMetricsSettings.HighPriorityEntitledTenantAction;

        // -------------------------------------------------
        // 10. Premium Entitlements
        // -------------------------------------------------

        if (c.PremiumTenant ||
            c.EnterpriseTenant)
            return _tokenMetricsSettings.TenantEntitlementAction;

        // -------------------------------------------------
        // 11. Governance Workloads
        // -------------------------------------------------

        if (c.ComplianceRequired ||
            c.AuditInvestigation)
            return _tokenMetricsSettings.GovernanceWorkloadAction;

        // -------------------------------------------------
        // Default
        // -------------------------------------------------

        return _tokenMetricsSettings.DefaultAction;
    }
}