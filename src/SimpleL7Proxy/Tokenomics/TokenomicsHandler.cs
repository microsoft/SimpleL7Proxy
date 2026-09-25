namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;
using System.Net;

public sealed class TokenomicsHandler : IConfigChangeSubscriber
{
    public IConcurrentPriQueue<RequestData> Queue { get; }
    public TokenMetricsCache TokenMetricsCache { get; }
    public TokenomicsSettings Settings { get; }
    private readonly ILogger<TokenomicsHandler> _logger;
    private readonly ProxyConfig _options;
    public bool doTokenomics { get; private set; }
    private int _minPriority=0;

    public TokenomicsHandler(
        IConcurrentPriQueue<RequestData> queue,
        TokenMetricsCache tokenMetricsCache,
        TokenomicsSettings settings,
        ILogger<TokenomicsHandler> logger,
        ProxyConfig options,
        ConfigChangeNotifier configChangeNotifier)
    {
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        TokenMetricsCache = tokenMetricsCache ?? throw new ArgumentNullException(nameof(tokenMetricsCache));
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
        doTokenomics = _options.TokenomicsEnable && Settings.TryParse(_options.TokenomicsOptions);
        // 0 is the highest priority
        _minPriority = _options.PriorityValues.Max();
    }

    public Task OnConfigChangedAsync(
        IReadOnlyList<ConfigChange> changes,
        ProxyConfig backendOptions,
        CancellationToken cancellationToken)
    {
        InitVars();

        _logger.LogInformation("Tokenomics service is {Status}", doTokenomics ? "enabled" : "disabled");

        return Task.CompletedTask;
    }

    public (ModelOverrideEnum, String) ProcessRequest(RequestData data)
    {
        (string conditionString, TokenActionEnum action) = Evaluate(data);

        switch (action)
        {
            case TokenActionEnum.IncreasePriority:
                data.Priority = Math.Min(data.Priority - 1, 0);
                throw new S7PRequeueException("Request delayed due to policy", now: true);

            case TokenActionEnum.DecreasePriority:
                data.Priority = Math.Max(data.Priority + 1, _minPriority);
                throw new S7PRequeueException("Request delayed due to policy", now: true);

            case TokenActionEnum.Reject:
                throw new ProxyErrorException(ProxyErrorException.ErrorType.NotEnqueued,
                                              (HttpStatusCode)429,
                                              "Message rejected due to policy.");

            // Scheduling
            case TokenActionEnum.Requeue:
                throw new S7PRequeueException("Request delayed due to policy", now: true);

            case TokenActionEnum.Delay:
                throw new S7PRequeueException("Request delayed due to policy", now: true, retry_after: Settings.DelayDuration);

            case TokenActionEnum.WaitForReset:
                throw new S7PRequeueException("Request delayed due to policy", now: true);

            // Token governance
            case TokenActionEnum.Throttle:
                throw new S7PThrottledException("Message throttled due to policy", now: true);
            case TokenActionEnum.Bypass:
                return (ModelOverrideEnum.None, String.Empty);

            case TokenActionEnum.ChangeModel:
                return (ModelOverrideEnum.Override, Settings.DefaultModel);

            case TokenActionEnum.UpgradeModel:
                return (ModelOverrideEnum.Upgrade,  String.Empty); // do it later when the modelname is known

            case TokenActionEnum.DowngradeModel:
                return (ModelOverrideEnum.Downgrade, String.Empty); // do it later when the modelname is known

        }

        return (ModelOverrideEnum.None, String.Empty);

        //     // Model routing
        //     case TokenActionEnum.IncreaseLimit:
        //     case TokenActionEnum.DecreaseLimit:
        //     case TokenActionEnum.Cap:

    }
    public (string, TokenActionEnum) Evaluate(RequestData data)
    {
        if (!doTokenomics)
            return ("TokenomicsDisabled", TokenActionEnum.None);

        TokenomicsCondition c = new TokenomicsCondition(data, Settings, TokenMetricsCache, Queue);

        if (c.AbuseDetected)
            return ("AbuseDetected", Settings.AbuseDetectedAction);

        if (c.MonthlyQuotaExceeded && !c.AdministratorOverride && !c.ApprovedException)
            return ("MonthlyQuotaExceeded", Settings.MonthlyQuotaExceededAction);

        if (c.AdministratorOverride || c.ApprovedException)
            return ("AdministratorOverride", Settings.AdministratorOverrideAction);

        if (c.DailyQuotaExceeded)
        {
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
