namespace SimpleL7Proxy.Tokenomics;

using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Proxy;
using System.Net;
using SharedParser.Tokenomics.LLM.Catalog;

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

    /// <summary>
    /// Determines if the specified host supports the given model and API.
    /// </summary>
    /// <param name="model">The model to check.</param>
    /// <param name="hostKey">The host key to check.</param>
    /// <param name="apiName">The API name to check.</param>
    /// <returns>True if the host supports the model and API; otherwise, false.</returns>
    public string? SupportedHostPath(string model, string hostKey, LLMApiEnum apiName, string originalPath)
    {
        
        // Console.WriteLine($"Checking supported host path for model: {model}, hostKey: {hostKey}, apiName: {apiName}, originalPath: {originalPath}");
        // nothing defined for this model, return the original path
        if (!Settings.ModelHostOptions.TryGetValue(model, out var supportingHosts)) 
        {
            return originalPath;
        }

        // foreach (var host in supportingHosts)
        // {
        //     Console.WriteLine($"Supporting host: {host.Key}");
        //     foreach (var api in host.Value.APIs)
        //     {
        //         Console.WriteLine($"  API: {api.Key}");
        //     }
        // }

        // Check if the host supports the API for this model
        if (supportingHosts is null
            || !supportingHosts.TryGetValue(hostKey, out var hostOptions)
            || !hostOptions.Enabled
            || apiName == LLMApiEnum.Unknown
            || !hostOptions.APIs.TryGetValue(apiName, out var apiOptions))
        {
            // Console.WriteLine($"Host {hostKey} does not support API {apiName} for model {model}");
            return null;
        }

        // At this point, update the path
        if (apiOptions.PassThrough)
        {
            return originalPath;
        }

        if (apiOptions.OverridePath)
        {
            return apiOptions.Path;
        }

        if (!LLMApiEnumParser.PartialUrls.TryGetValue(apiName, out var defaultPath))
        {
            return null;
        }

        // if the path contains a model placeholder, replace it with the actual model
        return defaultPath.Contains("{model}", StringComparison.Ordinal)
            ? defaultPath.Replace(
                "{model}", Uri.EscapeDataString(model), StringComparison.Ordinal)
            : defaultPath;
    }


    public LLMApiEnum ModelApiName(string path) =>
        LLMApiEnumParser.TryParsePath(path, out var api) ? api : LLMApiEnum.Unknown;

    public LLMApiEnum UpdateModelPath(string model, string originalPath, string? hostKey = null)
    {
        return ModelApiName(originalPath);
    }

    public async Task<ModelOverrideEnum> ProcessRequestAsync(RequestData data)
    {
        TokenomicsCondition c = await new TokenomicsCondition().CreateAsync(data, Settings, LiveMetrics, Queue);

        var (rulename, action) = await EvaluateAsync(data, c);

        data.Workload = data.Workload == "unknown" ? Settings.DefaultWorkload : data.Workload;

        data.EvaluationSequence++;

        data.TokenomicsSummary = new TokenomicsSummaryEvent(
            requestId: data.MID,
            userId: data.UserID,
            workload: data.Workload,
            evaluationSequence: data.EvaluationSequence,
            policyCondition: rulename,
            policyAction: action,
            requestedModel: data.EffectiveModel,
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
                    data.EffectiveModel = Settings.DefaultModel;
                    data.EffectiveAPIName = Settings.DefaultModelAPI;
                    data.TokenomicsSummary.SetModel(data.EffectiveModel);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                    break;

                case TokenActionEnum.UpgradeModel:
                    (data.EffectiveModel, data.EffectiveAPIName) = UpdateModelDestination(
                        data.EffectiveModel, data.EffectiveAPIName, ModelOverrideEnum.Upgrade);
                    data.TokenomicsSummary.SetModel(data.EffectiveModel);
                    data.TokenomicsSummary.SetDecision(TokenDecisionEnum.AllowedWithChanges, true);
                    break;

                case TokenActionEnum.DowngradeModel:
                    (data.EffectiveModel, data.EffectiveAPIName) = UpdateModelDestination(
                        data.EffectiveModel, data.EffectiveAPIName, ModelOverrideEnum.Downgrade);
                    data.TokenomicsSummary.SetModel(data.EffectiveModel);
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
        if (!doTokenomics)              return ("TokenomicsDisabled", TokenActionEnum.None);
        if (c.AbuseDetected)            return ("AbuseDetected", Settings.AbuseDetectedAction);
        if (data.IsOverride)            return ("Administrator Override", Settings.AdministratorOverrideAction);

        var (actionType, actions) = data.IsCriticalWorkload ? ("Critical ", Settings.PrivilegedExceedActions)
            : data.IsPremiumWorkload ? ("Premium ", Settings.PremiumExceedActions)
            : data.EffectiveModel is not null && Settings.ModelExceedActions.TryGetValue(data.EffectiveModel, out var modelActions)
                ? ("Model ", modelActions) : ("", Settings.DefaultExceededActions);

        if (c.HourlyQuotaExceeded )     return ($"{actionType}Hourly Quota Exceeded", actions.HourlyLimitAction);
        if (c.DailyQuotaExceeded)       return ($"{actionType}Daily Quota Exceeded", actions.DailyLimitAction);
        if (c.MonthlyQuotaExceeded)     return ($"{actionType}Monthly Quota Exceeded", actions.MonthlyLimitAction);
        if (c.HourlyBudgetExceeded)     return ($"{actionType}Hourly Budget Exceeded", actions.HourlyBudgetExceededAction);
        if (c.DailyBudgetExceeded)      return ($"{actionType}Daily Budget Exceeded", actions.DailyBudgetExceededAction);
        if (c.MonthlyBudgetExceeded)    return ($"{actionType}Monthly Budget Exceeded", actions.MonthlyBudgetExceededAction);

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
        return UpdateModelDestination(currentModel, LLMApiEnum.Unknown, modelOverride).Model;
    }

    /// <summary>Selects the adjacent model/API destination, retaining the current pair at hierarchy boundaries.</summary>
    public (string Model, LLMApiEnum APIName) UpdateModelDestination(
        string currentModel, LLMApiEnum currentAPIName, ModelOverrideEnum modelOverride)
    {
        if (string.IsNullOrWhiteSpace(currentModel)
            || modelOverride is not (ModelOverrideEnum.Upgrade or ModelOverrideEnum.Downgrade))
        {
            return (currentModel, currentAPIName);
        }

        List<ModelHierarchyEntry>? matchingModels = null;
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
            return (currentModel, currentAPIName);
        }

        var destinations = matchingModels.GroupBy(entry => entry.Model, StringComparer.OrdinalIgnoreCase).ToList();
        for (int index = 0; index < destinations.Count; index++)
        {
            if (currentModel.Equals(destinations[index].Key, StringComparison.OrdinalIgnoreCase))
            {
                int updatedIndex = modelOverride == ModelOverrideEnum.Upgrade
                    ? index - 1
                    : index + 1;
                if (updatedIndex < 0 || updatedIndex >= destinations.Count)
                {
                    return (currentModel, currentAPIName);
                }

                var destination = destinations[updatedIndex].FirstOrDefault(entry =>
                    currentAPIName != LLMApiEnum.Unknown && entry.APIName == currentAPIName)
                    ?? destinations[updatedIndex].FirstOrDefault(entry => entry.APIName == LLMApiEnum.Unknown)
                    ?? destinations[updatedIndex].First();
                return (destination.Model, destination.APIName == LLMApiEnum.Unknown
                    ? currentAPIName : destination.APIName);
            }
        }

        return (currentModel, currentAPIName);
    }

}
