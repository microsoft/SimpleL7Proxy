namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;
using System.Net;

public sealed class TokenomicsProcessor : IConfigChangeSubscriber
{
    public IConcurrentPriQueue<RequestData> Queue { get; }
    public TokenMetricsCache TokenMetricsCache { get; }
    public TokenomicsSettings Settings { get; }
    private readonly ILogger<TokenomicsProcessor> _logger;
    private readonly ProxyConfig _options;
    public bool doTokenomics { get; private set; }
    private int _minPriority=0;

    public TokenomicsProcessor(
        IConcurrentPriQueue<RequestData> queue,
        TokenMetricsCache tokenMetricsCache,
        TokenomicsSettings settings,
        ILogger<TokenomicsProcessor> logger,
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

    public void TokenWork(RequestData data)
    {
        (string conditionString, TokenActionEnum action) = Evaluate(data);

        switch (action)
        {
            case TokenActionEnum.IncreasePriority:
                data.Priority = Math.Min(data.Priority - 1, 0);
                break;
            case TokenActionEnum.DecreasePriority:
                data.Priority = Math.Max(data.Priority + 1, _minPriority);
                break;
            case TokenActionEnum.Reject:
                throw new ProxyErrorException(ProxyErrorException.ErrorType.NotEnqueued,
                                              (HttpStatusCode)429,
                                              "Message rejected due to policy.");


            // Scheduling
            case TokenActionEnum.Requeue:
            case TokenActionEnum.Delay:
            case TokenActionEnum.WaitForReset:
                throw new S7PRequeueException("Request delayed due to policy", now: true);


            // Model routing
            case TokenActionEnum.ChangeModel:
                break;
            case TokenActionEnum.UpgradeModel:
                break;
            case TokenActionEnum.DowngradeModel:
                break;

            // Token governance
            case TokenActionEnum.Throttle:
                throw new S7PThrottledException("Message throttled due to policy", now: true);

            case TokenActionEnum.IncreaseLimit:
                break;
            case TokenActionEnum.DecreaseLimit:
                break;
            case TokenActionEnum.Cap:
                break;
            case TokenActionEnum.Bypass:
            case TokenActionEnum.None:
            default:
                break;
        }
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

    public (string, TokenActionEnum) EvaluateBeforeQuery(TokenomicsCondition c)
    {
        ArgumentNullException.ThrowIfNull(c);

        if (c.PreferredModelUnavailable)
        {
            if (c.ModelReplacementAllowed)
                return ("PreferredModelReplacement", Settings.PreferredModelReplacementAction);

            return ("PreferredModelUnavailable", Settings.PreferredModelUnavailableAction);
        }

        if (c.LargeContextRequest)
        {
            if (c.CapacityAvailable)
                return ("LargeContextCapacityAvailable", Settings.LargeContextCapacityAvailableAction);

            return ("LargeContextRequest", Settings.LargeContextRequestAction);
        }

        if (c.CriticalPriority)
            return ("CriticalPriority", Settings.CriticalPriorityAction);

        if (c.HighPriority && (c.PremiumTenant || c.EnterpriseTenant))
            return ("HighPriorityEntitledTenant", Settings.HighPriorityEntitledTenantAction);

        if (c.PremiumTenant || c.EnterpriseTenant)
            return ("TenantEntitlement", Settings.TenantEntitlementAction);

        if (c.ComplianceRequired || c.AuditInvestigation)
            return ("GovernanceWorkload", Settings.GovernanceWorkloadAction);

        return ("Default", Settings.DefaultAction);
    }
}
