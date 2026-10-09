namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;
using System.Net;

public sealed class TokenomicsHandler : IConfigChangeSubscriber
{
    public IConcurrentPriQueue<RequestData> Queue { get; }
    public TokenMetricsCache TokenMetricsCache { get; }
    public LiveMetrics LiveMetrics { get; }
    public TokenomicsSettings Settings { get; }
    private readonly ILogger<TokenomicsHandler> _logger;
    private readonly ProxyConfig _options;
    public bool doTokenomics { get; private set; }
    private int _minPriority = 0;

    public TokenomicsHandler(
        IConcurrentPriQueue<RequestData> queue,
        TokenMetricsCache tokenMetricsCache,
        TokenomicsSettings settings,
        LiveMetrics liveMetrics,
        ILogger<TokenomicsHandler> logger,
        ProxyConfig options,
        ConfigChangeNotifier configChangeNotifier)
    {
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        TokenMetricsCache = tokenMetricsCache ?? throw new ArgumentNullException(nameof(tokenMetricsCache));
        LiveMetrics = liveMetrics ?? throw new ArgumentNullException(nameof(liveMetrics));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));


        InitVars();

        configChangeNotifier.Subscribe(this,
           [options => options.TokenomicsEnable,
            options => options.TokenomicsOptions]);
    }

    public void InitVars()
    {
        doTokenomics = _options.TokenomicsEnable;
        // 0 is the highest priority
        _minPriority = _options.PriorityValues.Max();
    }

    public Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        ProxyConfig backendOptions,
        CancellationToken cancellationToken)
    {
        InitVars();

        return Task.CompletedTask;
    }

    public decimal RequestCost(string model, int inputTokens, int cachedTokens, int outputTokens)
    {
        if (Settings.ModelCostPerToken.TryGetValue(model, out var modelPricing) &&
            modelPricing is not null)
        {
            return Math.Max(0, inputTokens - cachedTokens) * modelPricing.Input
                + cachedTokens * modelPricing.CachedInput
                + outputTokens * modelPricing.Output;
        }

        return 0m;
    }

    public async Task<ModelOverrideEnum> ProcessRequestAsync(RequestData data)
    {
        TokenomicsCondition c = await new TokenomicsCondition().CreateAsync(data, Settings, LiveMetrics, Queue);

        var (rulename, action) = await EvaluateAsync(data, c);

        data.Tenant = data.Tenant == "unknown" ? Settings.DefaultTenant : data.Tenant;

        data.EvaluationSequence++;

        data.TokenomicsSummary = new TokenomicsSummaryEvent(
            requestId: data.MID,
            userId: data.UserID,
            tenant: data.Tenant,
            evaluationSequence: data.EvaluationSequence,
            policyCondition: rulename,
            policyAction: action,
            requestedModel: data.Model,
            modelBefore: data.OriginalModel,
            priorityBefore: data.Priority);

        try
        {
            switch (action)
            {
                case TokenActionEnum.IncreasePriority:
                    data.Priority = Math.Min(data.Priority - 1, 0);
                    data.TokenomicsSummary.SetPriority(data.Priority);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Requeued, false);
                    throw new S7PRequeueException("Request delayed due to policy", now: true);

                case TokenActionEnum.DecreasePriority:
                    data.Priority = Math.Max(data.Priority + 1, _minPriority);
                    data.TokenomicsSummary.SetPriority(data.Priority);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Requeued, false);
                    throw new S7PRequeueException("Request delayed due to policy", now: true);

                case TokenActionEnum.Reject:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Rejected, false);
                    data.TokenomicsSummary.SetLocalStatus(403);
                    throw new S7PRejectedException("Message rejected due to policy.");

                // Scheduling
                case TokenActionEnum.Requeue:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Requeued, false);
                    throw new S7PRequeueException("Request delayed due to policy", now: true);

                case TokenActionEnum.Delay:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Delayed, false);
                    data.TokenomicsSummary.SetRetryAfter(Settings.DelayDuration);
                    throw new S7PRequeueException("Request delayed due to policy", now: true, retry_after: Settings.DelayDuration);

                case TokenActionEnum.WaitForReset:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Delayed, false);
                    throw new S7PRequeueException("Request delayed due to policy", now: true);

                // Token governance
                case TokenActionEnum.Throttle:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Throttled, false);
                    throw new S7PThrottledException("Message throttled due to policy", now: true);


                // MOST OF THE TIME CASE
                case TokenActionEnum.Bypass:
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.Allowed, true);
                    break;

                case TokenActionEnum.ChangeModel:
                    data.Model = Settings.DefaultModel;
                    data.TokenomicsSummary.SetModel(data.Model);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                    break;

                case TokenActionEnum.UpgradeModel:
                    data.Model = UpdateModel(data.Model, ModelOverrideEnum.Upgrade);
                    data.TokenomicsSummary.SetModel(data.Model);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                    break;

                case TokenActionEnum.DowngradeModel:
                    data.Model = UpdateModel(data.Model, ModelOverrideEnum.Downgrade);
                    data.TokenomicsSummary.SetModel(data.Model);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                    break;
            }


            return ModelOverrideEnum.None;
        }
        finally
        {
            data.TokenomicsSummary.Emit();
        }
    }

    public async Task<(string, TokenActionEnum)> EvaluateAsync(RequestData data, TokenomicsCondition c)
    {
        if (!doTokenomics)
            return ("TokenomicsDisabled", TokenActionEnum.None);


        if (c.AbuseDetected)
            return ("AbuseDetected", Settings.AbuseDetectedAction);

        if (c.MonthlyQuotaExceeded && !c.AdministratorOverride && !c.ApprovedException)
            return ("MonthlyQuotaExceeded", Settings.MonthlyQuotaExceededAction);

        if (c.AdministratorOverride || c.ApprovedException)
            return ("AdministratorOverride", Settings.AdministratorOverrideAction);

        if (c.DailyQuotaExceeded)
        {
            Console.WriteLine("Daily quota exceeded for user: " + data.UserID);

            if (c.IncidentResponse || c.AuditInvestigation || c.ComplianceRequired)
                return ("DailyQuotaGovernance", Settings.DailyQuotaGovernanceAction);

            return ("DailyQuotaExceeded", Settings.DailyQuotaExceededAction);
        }

        if (c.MonthlyBudgetExceeded)
        {
            if (c.PremiumTenant || c.EnterpriseTenant)
                return ("MonthlyBudgetEntitledTenant", Settings.MonthlyBudgetEntitledTenantAction);

            return ("MonthlyBudgetExceeded", Settings.MonthlyBudgetExceededAction);
        }

        if (c.DailyBudgetExceeded)
        {
            if (c.PremiumTenant || c.EnterpriseTenant)
                return ("DailyBudgetEntitledTenant", Settings.DailyBudgetEntitledTenantAction);

            return ("DailyBudgetExceeded", Settings.DailyBudgetExceededAction);
        }

        if (c.CapacityConstrained)
        {
            if (c.CriticalPriority || c.IncidentResponse)
                return ("CapacityCriticalWorkload", Settings.CapacityCriticalWorkloadAction);

            if (c.HighPriority)
                return ("CapacityHighPriority", Settings.CapacityHighPriorityAction);

            return ("CapacityConstrained", Settings.CapacityConstrainedAction);
        }

        if (c.QueueDepthHigh)
        {
            if (c.CriticalPriority)
                return ("QueueCriticalPriority", Settings.QueueCriticalPriorityAction);

            return ("QueueDepthHigh", Settings.QueueDepthHighAction);
        }

        return ("Default", Settings.DefaultAction);
    }

    public string UpdateModel(string currentModel, ModelOverrideEnum modelOverride)
    {
        if (string.IsNullOrWhiteSpace(currentModel)
            || modelOverride is not (ModelOverrideEnum.Upgrade or ModelOverrideEnum.Downgrade))
        {
            return currentModel;
        }

        List<string>? matchingModels = null;
        int longestPrefixLength = -1;
        foreach (var hierarchy in Settings.ModelHierarchy)
        {
            if (!string.IsNullOrWhiteSpace(hierarchy.Key)
                && hierarchy.Key.Length > longestPrefixLength
                && currentModel.StartsWith(hierarchy.Key, StringComparison.OrdinalIgnoreCase))
            {
                matchingModels = hierarchy.Value;
                longestPrefixLength = hierarchy.Key.Length;
            }
        }

        if (matchingModels == null)
        {
            return currentModel;
        }

        for (int index = 0; index < matchingModels.Count; index++)
        {
            if (currentModel.Equals(matchingModels[index], StringComparison.OrdinalIgnoreCase))
            {
                int updatedIndex = modelOverride == ModelOverrideEnum.Upgrade
                    ? index - 1
                    : index + 1;
                return updatedIndex >= 0 && updatedIndex < matchingModels.Count
                    ? matchingModels[updatedIndex]
                    : currentModel;
            }
        }

        return currentModel;
    }

}
