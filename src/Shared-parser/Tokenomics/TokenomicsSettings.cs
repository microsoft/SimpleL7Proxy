using System.Globalization;
using System.Reflection;
using SimpleL7Proxy.Extensions;

namespace SimpleL7Proxy.Tokenomics;


public class TokenomicsSettings
{
    /// <summary>Gets or sets the default token limits and USD budgets, serialized using their unprefixed property names.</summary>
    public LimitsAndBudgets DefaultLimitsAndBudgets { get; set; } = new();
    /// <summary>Gets or sets default actions, serialized using their unprefixed property names.</summary>
    public ExceededActions DefaultExceededActions { get; set; } = new();
    /// <summary>Gets or sets premium workload limits, serialized as PremiumWorkloadLimits.Field=value.</summary>
    public LimitsAndBudgets PremiumWorkloadLimits { get; set; } = new();
    /// <summary>Gets or sets premium workload actions, serialized as PremiumWorkloadLimitActions.Field=value.</summary>
    public ExceededActions PremiumWorkloadLimitActions { get; set; } = new();
    /// <summary>Gets or sets privileged workload limits, serialized as PrivilegedLimitsAndBudgets.Field=value.</summary>
    public LimitsAndBudgets PrivilegedLimitsAndBudgets { get; set; } = new();
    /// <summary>Gets or sets privileged workload actions, serialized as PrivilegedExceedActions.Field=value.</summary>
    public ExceededActions PrivilegedExceedActions { get; set; } = new();


    /// <summary>Gets or sets token limits and USD budgets keyed by model name.</summary>
    public Dictionary<string, LimitsAndBudgets> ModelLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ExceededActions> ModelLimitActions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the delay duration in milliseconds.</summary>
    public int DelayDuration { get; set; } = (int)TimeSpan.FromSeconds(10).TotalMilliseconds;

    public double CurrentCapacityUtilizationPercent { get; set; }=0.0;
    public double CapacityConstraintThresholdPercent { get; set; }=80.0;

    public float HighQueueDepthThreshold { get; set; }=0.90f;

    public int LargeContextThreshold { get; set; }=2048;
    public int CriticalPriority { get; set; }=1;
    public int HighPriority { get; set; }=2;

    /// <summary>Gets or sets the tenant used when a request does not provide one.</summary>
    public string DefaultTenant { get; set; } = "Contoso";

    /// <summary>Gets or sets the default model name; an empty value leaves it unconfigured.</summary>
    public string DefaultModel { get; set; } = string.Empty;
    public string DefaultModelAPI { get; set; } = string.Empty;

    /// <summary>Gets or sets input, cached-input, and output USD prices per token for each priced model; unlisted models are excluded from spend totals.</summary>
    public Dictionary<string, ModelTokenPricing> ModelCostPerToken { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gets or sets model lists keyed by hierarchy prefix.</summary>
    public Dictionary< string, List<string>> ModelHierarchy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, TokenomicsHostOptions> HostOptions { get; set; } = new();
    
    /// <summary>Gets or sets host assignments, enablement, and API path options keyed by model and host key.</summary>
    public Dictionary<string, Dictionary<string, TokenomicsHostOptions>> ModelHostOptions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);


    /// <summary>Gets or sets the action when abuse is detected.</summary>
    public TokenActionEnum AbuseDetectedAction { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the action for an administrator override or approved exception.</summary>
    public TokenActionEnum AdministratorOverrideAction { get; set; } = TokenActionEnum.Bypass;


    // Critical Workload
    public TokenActionEnum CapacityCriticalWorkloadAction { get; set; } = TokenActionEnum.IncreasePriority;

    /// <summary>Gets or sets the capacity-constrained action for other high-priority work.</summary>
    public TokenActionEnum CapacityHighPriorityAction { get; set; } = TokenActionEnum.Requeue;

    /// <summary>Gets or sets the capacity-constrained action for other workloads.</summary>
    public TokenActionEnum CapacityConstrainedAction { get; set; } = TokenActionEnum.Delay;

    /// <summary>Gets or sets the high-queue-depth action for critical-priority work.</summary>
    public TokenActionEnum QueueCriticalPriorityAction { get; set; } = TokenActionEnum.IncreasePriority;

    /// <summary>Gets or sets the high-queue-depth action for other workloads.</summary>
    public TokenActionEnum QueueDepthHighAction { get; set; } = TokenActionEnum.Requeue;

    /// <summary>Gets or sets the action when the preferred model is unavailable and replacement is allowed.</summary>
    public TokenActionEnum PreferredModelReplacementAction { get; set; } = TokenActionEnum.ChangeModel;

    /// <summary>Gets or sets the action when the preferred model is unavailable and replacement is not allowed.</summary>
    public TokenActionEnum PreferredModelUnavailableAction { get; set; } = TokenActionEnum.Requeue;

    /// <summary>Gets or sets the large-context action when capacity is available.</summary>
    public TokenActionEnum LargeContextCapacityAvailableAction { get; set; } = TokenActionEnum.UpgradeModel;

    /// <summary>Gets or sets the large-context action when capacity is not available.</summary>
    public TokenActionEnum LargeContextRequestAction { get; set; } = TokenActionEnum.ChangeModel;

    /// <summary>Gets or sets the critical-priority action when no earlier policy matches.</summary>
    public TokenActionEnum CriticalPriorityAction { get; set; } = TokenActionEnum.IncreasePriority;

    /// <summary>Gets or sets the high-priority action for premium or enterprise tenants when no earlier policy matches.</summary>
    public TokenActionEnum HighPriorityEntitledTenantAction { get; set; } = TokenActionEnum.IncreasePriority;

    /// <summary>Gets or sets the premium or enterprise entitlement action when no earlier policy matches.</summary>
    public TokenActionEnum TenantEntitlementAction { get; set; } = TokenActionEnum.IncreaseLimit;

    /// <summary>Gets or sets the compliance or audit action when no earlier policy matches.</summary>
    public TokenActionEnum GovernanceWorkloadAction { get; set; } = TokenActionEnum.Bypass;

    /// <summary>Gets or sets the action when no policy matches.</summary>
    public TokenActionEnum DefaultAction { get; set; } = TokenActionEnum.Bypass;


    
    private static readonly PropertyInfo[] SettableProperties =
        typeof(TokenomicsSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod?.IsPublic == true)
            .ToArray();

    private static readonly string ModelCostPrefix = nameof(ModelCostPerToken) + ".";

    /// <summary>Gets the field names ignored by the last parse because their names or values were invalid.</summary>
    public IReadOnlyList<string> IgnoredFields { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Applies recognized comma- or semicolon-separated key=value pairs. Unprefixed limit and action
    /// keys target defaults; workload keys use their container name, such as
    /// PrivilegedExceedActions.DailyLimitAction=bypass or PremiumWorkloadLimits.DailyTokenLimit=5000.
    /// Model overrides retain ModelLimits=model=Field:value and ModelLimitActions=model=Field:value syntax.
    /// Omitted or invalid fields retain their existing values. Invalid entries are skipped and reported
    /// in IgnoredFields; a failed compound entry cannot apply partial changes. Returns false only for
    /// empty input or input that cannot be split into entries.
    /// </summary>
    public bool TryParse(string data) {
        IgnoredFields = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(data)) {
            return false;
        }

        if (!TrySplitParts(data, out var parts) || parts.Count == 0) {
            return false;
        }

        var pending = CloneForParse();
        var ignoredFields = new List<string>();
        var applied = false;
        foreach (var part in parts) {
            var candidate = pending.CloneForParse();
            if (candidate.TryApplyPart(part)) {
                pending = candidate;
                applied = true;
            } else {
                ignoredFields.Add(TrySplitKeyValue(part, out var key, out _) ? key : "(malformed entry)");
            }
        }

        if (applied) {
            CopyFrom(pending);
        }
        IgnoredFields = ignoredFields.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        return true;
    }

    /// <summary>
    /// Splits on <c>;</c> and <c>,</c> except commas within a ModelHierarchy value.
    /// </summary>
    private static bool TrySplitParts(string data, out List<string> parts) {
        parts = [];
        var partStart = 0;
        var hierarchyBracketDepth = 0;
        var isModelHierarchyPart = false;

        for (var index = 0; index < data.Length; index++) {
            var currentCharacter = data[index];

            if (!isModelHierarchyPart && currentCharacter == '=') {
                var partKey = data[partStart..index].Trim();
                isModelHierarchyPart = partKey.Equals(nameof(ModelHierarchy), StringComparison.OrdinalIgnoreCase);
            }

            if (isModelHierarchyPart) {
                if (currentCharacter == '[') {
                    hierarchyBracketDepth++;
                } else if (currentCharacter == ']') {
                    if (hierarchyBracketDepth == 0) {
                        return false;
                    }

                    hierarchyBracketDepth--;
                }
            }

            var isSeparator = currentCharacter == ';';
            if (currentCharacter == ',') {
                isSeparator = !isModelHierarchyPart || hierarchyBracketDepth == 0;

                if (isSeparator && isModelHierarchyPart) {
                    var remainingData = data[(index + 1)..];
                    var nextColon = remainingData.IndexOf(':');
                    var nextEquals = remainingData.IndexOf('=');
                    isSeparator = nextEquals >= 0 && (nextColon < 0 || nextEquals < nextColon);
                }
            }

            if (!isSeparator) {
                continue;
            }

            if (isModelHierarchyPart && hierarchyBracketDepth != 0) {
                return false;
            }

            var part = data[partStart..index].Trim();
            if (part.Length > 0) {
                parts.Add(part);
            }

            partStart = index + 1;
            hierarchyBracketDepth = 0;
            isModelHierarchyPart = false;
        }

        if (isModelHierarchyPart && hierarchyBracketDepth != 0) {
            return false;
        }

        var finalPart = data[partStart..].Trim();
        if (finalPart.Length > 0) {
            parts.Add(finalPart);
        }

        return true;
    }

    private static bool TrySplitKeyValue(string part, out string key, out string value) {
        var splitIndex = part.IndexOf('=');
        if (splitIndex <= 0) {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = part[..splitIndex].Trim();
        value = part[(splitIndex + 1)..].Trim();
        return key.Length > 0;
    }

    private bool TryApplyPart(string part) {
        if (!TrySplitKeyValue(part, out var key, out var value)) {
            return false;
        }

        if (key.Equals(nameof(HostOptions), StringComparison.OrdinalIgnoreCase)) {
            return TryParseHostOptions(value);
        }

        if (key.Equals(nameof(ModelHostOptions), StringComparison.OrdinalIgnoreCase)) {
            return TryParseModelHostOptions(value);
        }

        if (key.Equals(nameof(ModelLimits), StringComparison.OrdinalIgnoreCase)
            || key.Equals(nameof(ModelLimitActions), StringComparison.OrdinalIgnoreCase)) {
            if (!TrySplitKeyValue(value, out var encodedModel, out var encodedFields)) {
                return false;
            }

            var model = Uri.UnescapeDataString(encodedModel);
            if (string.IsNullOrWhiteSpace(model)) {
                return false;
            }

            object modelOptions;
            if (key.Equals(nameof(ModelLimits), StringComparison.OrdinalIgnoreCase)) {
                if (!ModelLimits.TryGetValue(model, out var limits)) {
                    limits = new LimitsAndBudgets();
                    ModelLimits[model] = limits;
                }
                modelOptions = limits;
            } else {
                if (!ModelLimitActions.TryGetValue(model, out var actions)) {
                    actions = new ExceededActions();
                    ModelLimitActions[model] = actions;
                }
                modelOptions = actions;
            }

            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in encodedFields.Split('|', StringSplitOptions.TrimEntries)) {
                var fieldParts = field.Split(':', 2, StringSplitOptions.TrimEntries);
                if (fieldParts.Length != 2 || !fieldNames.Add(fieldParts[0])
                    || !TryParseScalar(fieldParts[0], fieldParts[1], modelOptions)) {
                    return false;
                }
            }
            return true;
        }

        if (key.StartsWith(ModelCostPrefix, StringComparison.OrdinalIgnoreCase)) {
            return TryParseModelCost(key[ModelCostPrefix.Length..], value);
        }

        var dotIndex = key.IndexOf('.');
        if (dotIndex >= 0) {
            var scope = SettableProperties.FirstOrDefault(property =>
                property.Name.Equals(key[..dotIndex], StringComparison.OrdinalIgnoreCase)
                && (property.Name is nameof(PremiumWorkloadLimits) or nameof(PremiumWorkloadLimitActions)
                    or nameof(PrivilegedLimitsAndBudgets) or nameof(PrivilegedExceedActions)));
            return scope?.GetValue(this) is { } target
                && TryParseScalar(key[(dotIndex + 1)..], value, target);
        }

        if (key.Equals(nameof(ModelHierarchy), StringComparison.OrdinalIgnoreCase)) {
            return TryParseHierarchy(value);
        }

        if (key.Equals(nameof(DelayDuration), StringComparison.OrdinalIgnoreCase)) {
            if (!TryParseDelayDuration(value, out var milliseconds)) {
                return false;
            }

            DelayDuration = milliseconds;
            return true;
        }

        return TryParseScalar(key, value);
    }

    private bool TryParseHostOptions(string value) {
        if (!TrySplitKeyValue(value, out var encodedHost, out var encodedOptions)) {
            return false;
        }

        var host = Uri.UnescapeDataString(encodedHost);
        if (string.IsNullOrWhiteSpace(host)) {
            return false;
        }

        if (!TryParseHostOptionValue(encodedOptions, out var hostOptions)) {
            return false;
        }

        HostOptions[host] = hostOptions;
        return true;
    }

    private bool TryParseModelHostOptions(string value) {
        if (!TrySplitKeyValue(value, out var encodedModel, out var encodedHostOptions) ||
            !TrySplitKeyValue(encodedHostOptions, out var encodedHost, out var encodedOptions)) {
            return false;
        }

        var model = Uri.UnescapeDataString(encodedModel);
        var host = Uri.UnescapeDataString(encodedHost);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(host) ||
            !TryParseHostOptionValue(encodedOptions, out var hostOptions)) {
            return false;
        }

        if (!ModelHostOptions.TryGetValue(model, out var modelHosts)) {
            modelHosts = new Dictionary<string, TokenomicsHostOptions>(StringComparer.OrdinalIgnoreCase);
            ModelHostOptions[model] = modelHosts;
        }

        modelHosts[host] = hostOptions;
        return true;
    }

    private static bool TryParseHostOptionValue(string encodedOptions, out TokenomicsHostOptions hostOptions) {
        hostOptions = null!;
        bool? enabled = null;
        var apis = new Dictionary<string, TokenomicsApiOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var optionPart in encodedOptions.Split('|', StringSplitOptions.TrimEntries)) {
            var optionFields = optionPart.Split(':', 5, StringSplitOptions.None);
            if (optionFields.Length == 2 &&
                optionFields[0].Equals(nameof(TokenomicsHostOptions.Enabled), StringComparison.OrdinalIgnoreCase)) {
                if (enabled.HasValue || !bool.TryParse(optionFields[1], out var parsedEnabled)) {
                    return false;
                }

                enabled = parsedEnabled;
                continue;
            }

            if (optionFields.Length is >= 3 and <= 5 &&
                optionFields[0].Equals("Api", StringComparison.OrdinalIgnoreCase)) {
                var apiName = Uri.UnescapeDataString(optionFields[1]);
                if (string.IsNullOrWhiteSpace(apiName) ||
                    !bool.TryParse(optionFields[2], out var firstFlag) ||
                    apis.ContainsKey(apiName)) {
                    return false;
                }

                bool passThrough;
                bool overridePath;
                string? path;
                if (optionFields.Length >= 4 && bool.TryParse(optionFields[3], out var secondFlag)) {
                    passThrough = firstFlag;
                    overridePath = secondFlag;
                    path = optionFields.Length == 5
                        ? Uri.UnescapeDataString(optionFields[4])
                        : null;
                    if ((passThrough && (overridePath || path is not null))
                        || (overridePath && path is null)
                        || (!overridePath && path is not null)) {
                        return false;
                    }
                } else {
                    var legacyPath = optionFields.Length == 4
                        ? Uri.UnescapeDataString(optionFields[3])
                        : null;
                    passThrough = !firstFlag;
                    overridePath = firstFlag && legacyPath is not null;
                    path = overridePath ? legacyPath : null;
                }

                apis.Add(apiName, new TokenomicsApiOptions {
                    PassThrough = passThrough,
                    OverridePath = overridePath,
                    Path = path
                });
                continue;
            }

            return false;
        }

        if (!enabled.HasValue) {
            return false;
        }

        hostOptions = new TokenomicsHostOptions {
            Enabled = enabled.Value,
            APIs = apis
        };
        return true;
    }

    private bool TryParseModelCost(string encodedModel, string value) {
        var model = Uri.UnescapeDataString(encodedModel);
        if (string.IsNullOrWhiteSpace(model)) {
            return false;
        }

                ModelTokenPricing modelPricing;
                if (decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var legacyOutputCost)) {
                    if (legacyOutputCost < 0) {
                        return false;
                    }

                    modelPricing = new ModelTokenPricing { Output = legacyOutputCost };
                } else {
                    decimal? inputCost = null;
                    decimal? cachedInputCost = null;
                    decimal? outputCost = null;

                    foreach (var pricingPart in value.Split('|', StringSplitOptions.TrimEntries)) {
                        var pricingPair = pricingPart.Split(':', 2, StringSplitOptions.TrimEntries);
                        if (pricingPair.Length != 2 ||
                            !decimal.TryParse(pricingPair[1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var parsedCost) ||
                            parsedCost < 0) {
                            return false;
                        }

                        if (pricingPair[0].Equals(nameof(ModelTokenPricing.Input), StringComparison.OrdinalIgnoreCase)
                            && !inputCost.HasValue) {
                            inputCost = parsedCost;
                        } else if (pricingPair[0].Equals(nameof(ModelTokenPricing.CachedInput), StringComparison.OrdinalIgnoreCase)
                            && !cachedInputCost.HasValue) {
                            cachedInputCost = parsedCost;
                        } else if (pricingPair[0].Equals(nameof(ModelTokenPricing.Output), StringComparison.OrdinalIgnoreCase)
                            && !outputCost.HasValue) {
                            outputCost = parsedCost;
                        } else {
                            return false;
                        }
                    }

                    if (!inputCost.HasValue || !cachedInputCost.HasValue || !outputCost.HasValue) {
                        return false;
                    }

                    modelPricing = new ModelTokenPricing {
                        Input = inputCost.Value,
                        CachedInput = cachedInputCost.Value,
                        Output = outputCost.Value
                    };
                }

        ModelCostPerToken[model] = modelPricing;
        return true;
    }

    private bool TryParseHierarchy(string value) {
        var hierarchy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var hierarchyIndex = 0;

                while (hierarchyIndex < value.Length) {
                    while (hierarchyIndex < value.Length && char.IsWhiteSpace(value[hierarchyIndex])) {
                        hierarchyIndex++;
                    }

                    var colonIndex = value.IndexOf(':', hierarchyIndex);
                    if (colonIndex <= hierarchyIndex) {
                        return false;
                    }

                    var encodedPrefix = value[hierarchyIndex..colonIndex].Trim();
                    var prefix = Uri.UnescapeDataString(encodedPrefix);
                    if (string.IsNullOrWhiteSpace(prefix)) {
                        return false;
                    }

                    hierarchyIndex = colonIndex + 1;
                    while (hierarchyIndex < value.Length && char.IsWhiteSpace(value[hierarchyIndex])) {
                        hierarchyIndex++;
                    }

                    if (hierarchyIndex >= value.Length || value[hierarchyIndex] != '[') {
                        return false;
                    }

                    var closeBracketIndex = value.IndexOf(']', hierarchyIndex + 1);
                    if (closeBracketIndex < 0) {
                        return false;
                    }

                    var models = new List<string>();
                    var encodedModels = value[(hierarchyIndex + 1)..closeBracketIndex].Trim();
                    if (encodedModels.Length > 0) {
                        foreach (var encodedModel in encodedModels.Split(',', StringSplitOptions.TrimEntries)) {
                            var model = Uri.UnescapeDataString(encodedModel);
                            if (string.IsNullOrWhiteSpace(model)) {
                                return false;
                            }

                            models.Add(model);
                        }
                    }

                    hierarchy[prefix] = models;
                    hierarchyIndex = closeBracketIndex + 1;
                    while (hierarchyIndex < value.Length && char.IsWhiteSpace(value[hierarchyIndex])) {
                        hierarchyIndex++;
                    }

                    if (hierarchyIndex == value.Length) {
                        break;
                    }

                    if (value[hierarchyIndex] != ',') {
                        return false;
                    }

                    hierarchyIndex++;
                    if (hierarchyIndex == value.Length) {
                        return false;
                    }
                }

        ModelHierarchy = hierarchy;
        return true;
    }

    private static bool TryParseDelayDuration(string value, out int milliseconds) {
        milliseconds = 0;
        long totalMilliseconds;
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) {
                    if (!long.TryParse(value[..^2], NumberStyles.None, CultureInfo.InvariantCulture,
                            out totalMilliseconds) ||
                        totalMilliseconds < 0 ||
                        totalMilliseconds > int.MaxValue) {
                        return false;
                    }
                } else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase)) {
                    if (!long.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture,
                            out var totalSeconds) ||
                        totalSeconds < 0 ||
                        totalSeconds > int.MaxValue / 1000L) {
                        return false;
                    }

                    totalMilliseconds = totalSeconds * 1000L;
                } else {
                    var durationParts = value.Split(':');
                    if (durationParts.Length is < 2 or > 3) {
                        return false;
                    }

                    var minuteValue = 0L;
                    if (durationParts.Length == 3 &&
                        (!long.TryParse(durationParts[0], NumberStyles.None, CultureInfo.InvariantCulture,
                            out minuteValue) ||
                         minuteValue < 0 ||
                         minuteValue > int.MaxValue / 60000L)) {
                        return false;
                    }

                    if (!int.TryParse(durationParts[^2], NumberStyles.None, CultureInfo.InvariantCulture,
                            out var secondValue) ||
                        secondValue is < 0 or > 59 ||
                        durationParts[^1].Length is < 1 or > 3 ||
                        !int.TryParse(durationParts[^1], NumberStyles.None, CultureInfo.InvariantCulture,
                            out var millisecondValue) ||
                        millisecondValue is < 0 or > 999) {
                        return false;
                    }

                    totalMilliseconds = ((minuteValue * 60L) + secondValue) * 1000L + millisecondValue;
                    if (totalMilliseconds > int.MaxValue) {
                        return false;
                    }
                }

        milliseconds = (int)totalMilliseconds;
        return true;
    }

    private bool TryParseScalar(string key, string value, object? target = null) {
        var property = target is not null
            ? target.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            : typeof(TokenomicsSettings).GetProperty(key,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? typeof(LimitsAndBudgets).GetProperty(key,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? typeof(ExceededActions).GetProperty(key,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property?.SetMethod is null) {
            return false;
        }

        object parsedValue;
        if (property.PropertyType == typeof(string)) {
            parsedValue = Uri.UnescapeDataString(value);
            } else if (property.PropertyType == typeof(int) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)) {
                parsedValue = intValue;
            } else if (property.PropertyType == typeof(bool) && bool.TryParse(value, out var boolValue)) {
                parsedValue = boolValue;
            } else if (property.PropertyType == typeof(decimal) &&
                decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue)) {
                parsedValue = decimalValue;
            } else if (property.PropertyType == typeof(double) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) &&
                double.IsFinite(doubleValue)) {
                parsedValue = doubleValue;
            } else if (property.PropertyType == typeof(float) &&
                float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue) &&
                float.IsFinite(floatValue)) {
                parsedValue = floatValue;
            } else if (property.PropertyType == typeof(TimeSpan) &&
                TimeSpan.TryParseExact(value, "c", CultureInfo.InvariantCulture, out var timeSpanValue)) {
                parsedValue = timeSpanValue;
            } else if (property.PropertyType == typeof(TokenActionEnum) &&
                Enum.TryParse<TokenActionEnum>(value, true, out var action) && Enum.IsDefined(action) &&
                value.Equals(action.ToString(), StringComparison.OrdinalIgnoreCase)) {
                parsedValue = action;
            } else {
                return false;
            }

        property.SetValue(target ?? (property.DeclaringType == typeof(LimitsAndBudgets) ? (object)DefaultLimitsAndBudgets
            : property.DeclaringType == typeof(ExceededActions) ? DefaultExceededActions : this), parsedValue);
        return true;
    }

    /// <summary>Returns semicolon-separated settings with dotted workload fields, unprefixed defaults, invariant numbers, named actions, URI-escaped names and paths, and model-host assignments.</summary>
    public override string ToString() {
        var parts = new List<string>();
        foreach (var property in SettableProperties) {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod is null) {
                continue;
            }

            if (property.Name == nameof(DefaultLimitsAndBudgets)) {
                foreach (var limitProperty in typeof(LimitsAndBudgets).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                    parts.Add($"{limitProperty.Name}={FormatScalar(limitProperty.GetValue(DefaultLimitsAndBudgets)!)}");
                }
            } else if (property.Name == nameof(DefaultExceededActions)) {
                foreach (var actionProperty in typeof(ExceededActions).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                    parts.Add($"{actionProperty.Name}={FormatScalar(actionProperty.GetValue(DefaultExceededActions)!)}");
                }
            } else if (property.PropertyType == typeof(LimitsAndBudgets)
                || property.PropertyType == typeof(ExceededActions)) {
                var scope = property.GetValue(this)
                    ?? throw new InvalidOperationException($"{property.Name} cannot be null.");
                foreach (var field in property.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                    parts.Add($"{property.Name}.{field.Name}={FormatScalar(field.GetValue(scope)!)}");
                }
            } else if (property.Name is nameof(ModelLimits) or nameof(ModelLimitActions)) {
                var modelOptions = property.Name == nameof(ModelLimits)
                    ? ModelLimits.Select(entry => (Model: entry.Key, Value: (object)entry.Value))
                    : ModelLimitActions.Select(entry => (Model: entry.Key, Value: (object)entry.Value));
                foreach (var entry in modelOptions) {
                    if (string.IsNullOrWhiteSpace(entry.Model) || entry.Value is null) {
                        throw new InvalidOperationException($"{property.Name} model names and values cannot be null or whitespace.");
                    }
                    var fields = entry.Value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(field => $"{field.Name}:{FormatScalar(field.GetValue(entry.Value)!)}");
                    parts.Add($"{property.Name}={Uri.EscapeDataString(entry.Model)}={string.Join("|", fields)}");
                }
            } else if (property.Name is nameof(DefaultTenant) or nameof(DefaultModel) or nameof(DefaultModelAPI)) {
                parts.Add($"{property.Name}={Uri.EscapeDataString((string?)property.GetValue(this) ?? string.Empty)}");
            } else if (property.Name == nameof(DelayDuration)) {
                parts.Add($"{property.Name}={FormatDelayDuration()}");
            } else if (property.Name == nameof(ModelCostPerToken)) {
                FormatModelCosts(parts);
            } else if (property.Name == nameof(ModelHierarchy)) {
                parts.Add($"{property.Name}={FormatHierarchy()}");
            } else if (property.Name == nameof(HostOptions)) {
                FormatHostOptions(parts);
            } else if (property.Name == nameof(ModelHostOptions)) {
                FormatModelHostOptions(parts);
            } else {
                var value = property.GetValue(this);
                if (value is null) {
                    continue;
                }

                parts.Add($"{property.Name}={FormatScalar(value)}");
            }
        }

        return string.Join("; ", parts);
    }

    private string FormatDelayDuration() {
        if (DelayDuration < 0) {
            throw new InvalidOperationException($"{nameof(DelayDuration)} cannot be negative.");
        }

        var totalMinutes = DelayDuration / 60000;
        var seconds = DelayDuration / 1000 % 60;
        var milliseconds = DelayDuration % 1000;
        return totalMinutes > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{totalMinutes:D2}:{seconds:D2}:{milliseconds:D3}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds:D2}:{milliseconds:D3}");
    }

    private void FormatModelCosts(List<string> parts) {
        foreach (var (model, pricing) in ModelCostPerToken) {
            if (pricing is null ||
                pricing.Input < 0 ||
                pricing.CachedInput < 0 ||
                pricing.Output < 0) {
                throw new InvalidOperationException(
                    $"{nameof(ModelCostPerToken)} values cannot be null or negative.");
            }

            parts.Add(
                $"{nameof(ModelCostPerToken)}.{Uri.EscapeDataString(model)}=" +
                $"{nameof(ModelTokenPricing.Input)}:{pricing.Input.ToString(CultureInfo.InvariantCulture)}|" +
                $"{nameof(ModelTokenPricing.CachedInput)}:{pricing.CachedInput.ToString(CultureInfo.InvariantCulture)}|" +
                $"{nameof(ModelTokenPricing.Output)}:{pricing.Output.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private string FormatHierarchy() {
        return string.Join(", ", ModelHierarchy.Select(entry =>
            $"{Uri.EscapeDataString(entry.Key)}: [{string.Join(", ", entry.Value.Select(Uri.EscapeDataString))}]"));
    }

    private void FormatHostOptions(List<string> parts) {
        foreach (var (host, hostOptions) in HostOptions) {
            if (string.IsNullOrWhiteSpace(host)) {
                throw new InvalidOperationException(
                    $"{nameof(HostOptions)} host names cannot be null or whitespace.");
            }

            parts.Add(
                $"{nameof(HostOptions)}={Uri.EscapeDataString(host)}=" +
                FormatHostOptionValue(nameof(HostOptions), hostOptions));
        }
    }

    private void FormatModelHostOptions(List<string> parts) {
        foreach (var (model, modelHosts) in ModelHostOptions) {
            if (string.IsNullOrWhiteSpace(model) || modelHosts is null) {
                throw new InvalidOperationException(
                    $"{nameof(ModelHostOptions)} model names and host maps cannot be null or whitespace.");
            }

            foreach (var (host, hostOptions) in modelHosts) {
                if (string.IsNullOrWhiteSpace(host)) {
                    throw new InvalidOperationException(
                        $"{nameof(ModelHostOptions)} host names cannot be null or whitespace.");
                }

                parts.Add(
                    $"{nameof(ModelHostOptions)}={Uri.EscapeDataString(model)}={Uri.EscapeDataString(host)}=" +
                    FormatHostOptionValue(nameof(ModelHostOptions), hostOptions));
            }
        }
    }

    private static string FormatHostOptionValue(string settingName, TokenomicsHostOptions hostOptions) {
        if (hostOptions is null || hostOptions.APIs is null) {
            throw new InvalidOperationException($"{settingName} option values cannot be null.");
        }

        var optionParts = new List<string> {
            $"{nameof(TokenomicsHostOptions.Enabled)}:{hostOptions.Enabled.ToString().ToLowerInvariant()}"
        };
        foreach (var (apiName, apiOptions) in hostOptions.APIs) {
            if (string.IsNullOrWhiteSpace(apiName) || apiOptions is null) {
                throw new InvalidOperationException(
                    $"{settingName} API names and option values cannot be null or whitespace.");
            }

            var passThrough = apiOptions.PassThrough;
            var overridePath = !passThrough && apiOptions.OverridePath && apiOptions.Path is not null;
            var apiPart =
                $"Api:{Uri.EscapeDataString(apiName)}:{passThrough.ToString().ToLowerInvariant()}:{overridePath.ToString().ToLowerInvariant()}";
            if (overridePath) {
                apiPart += $":{Uri.EscapeDataString(apiOptions.Path!)}";
            }

            optionParts.Add(apiPart);
        }

        return string.Join("|", optionParts);
    }

    private static string FormatScalar(object value) {
        if (value is Enum enumValue) {
            var name = enumValue.ToString();
            return name.Length == 0
                ? name
                : char.ToLowerInvariant(name[0]) + name[1..];
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private TokenomicsSettings CloneForParse() {
        var clone = (TokenomicsSettings)MemberwiseClone();
        foreach (var property in SettableProperties.Where(property =>
            property.PropertyType == typeof(LimitsAndBudgets) || property.PropertyType == typeof(ExceededActions))) {
            var source = property.GetValue(this)
                ?? throw new InvalidOperationException($"{property.Name} cannot be null.");
            object scope = property.PropertyType == typeof(LimitsAndBudgets)
                ? new LimitsAndBudgets() : new ExceededActions();
            foreach (var field in property.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                field.SetValue(scope, field.GetValue(source));
            }
            property.SetValue(clone, scope);
        }
        clone.ModelLimits = ModelLimits.ToDictionary(entry => entry.Key, entry => new LimitsAndBudgets {
            MonthlyTokenLimit = entry.Value.MonthlyTokenLimit,
            DailyTokenLimit = entry.Value.DailyTokenLimit,
            HourlyTokenLimit = entry.Value.HourlyTokenLimit,
            HourlyBudgetUsd = entry.Value.HourlyBudgetUsd,
            DailyBudgetUsd = entry.Value.DailyBudgetUsd,
            MonthlyBudgetUsd = entry.Value.MonthlyBudgetUsd
        }, ModelLimits.Comparer);
        clone.ModelLimitActions = ModelLimitActions.ToDictionary(entry => entry.Key, entry => new ExceededActions {
            HourlyLimitAction = entry.Value.HourlyLimitAction,
            DailyLimitAction = entry.Value.DailyLimitAction,
            MonthlyLimitAction = entry.Value.MonthlyLimitAction,
            HourlyBudgetExceededAction = entry.Value.HourlyBudgetExceededAction,
            DailyBudgetExceededAction = entry.Value.DailyBudgetExceededAction,
            MonthlyBudgetExceededAction = entry.Value.MonthlyBudgetExceededAction
        }, ModelLimitActions.Comparer);
        clone.ModelCostPerToken = new Dictionary<string, ModelTokenPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ModelCostPerToken) {
            clone.ModelCostPerToken[pair.Key] = new ModelTokenPricing {
                Input = pair.Value.Input,
                CachedInput = pair.Value.CachedInput,
                Output = pair.Value.Output
            };
        }

        clone.ModelHierarchy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ModelHierarchy) {
            clone.ModelHierarchy[pair.Key] = [.. pair.Value];
        }

        clone.HostOptions = new Dictionary<string, TokenomicsHostOptions>(HostOptions.Comparer);
        foreach (var (host, hostOptions) in HostOptions) {
            clone.HostOptions[host] = CloneHostOptions(hostOptions);
        }

        clone.ModelHostOptions = new Dictionary<string, Dictionary<string, TokenomicsHostOptions>>(
            ModelHostOptions.Comparer);
        foreach (var (model, modelHosts) in ModelHostOptions) {
            var clonedHosts = new Dictionary<string, TokenomicsHostOptions>(modelHosts.Comparer);
            foreach (var (host, hostOptions) in modelHosts) {
                clonedHosts[host] = CloneHostOptions(hostOptions);
            }
            clone.ModelHostOptions[model] = clonedHosts;
        }

        return clone;
    }

    private static TokenomicsHostOptions CloneHostOptions(TokenomicsHostOptions hostOptions) {
        var clonedApis = new Dictionary<string, TokenomicsApiOptions>(hostOptions.APIs.Comparer);
        foreach (var (apiName, apiOptions) in hostOptions.APIs) {
            clonedApis[apiName] = new TokenomicsApiOptions {
                PassThrough = apiOptions.PassThrough,
                OverridePath = apiOptions.OverridePath,
                Path = apiOptions.Path
            };
        }

        return new TokenomicsHostOptions {
            Enabled = hostOptions.Enabled,
            APIs = clonedApis
        };
    }

    private void CopyFrom(TokenomicsSettings source) {
        foreach (var property in SettableProperties) {
            if (property.SetMethod is null) {
                continue;
            }

            property.SetValue(this, property.GetValue(source));
        }
    }
}
