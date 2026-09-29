using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>Defines USD prices for each token category supported by a model.</summary>
public sealed class ModelTokenPricing
{
    /// <summary>Gets or sets the USD price per non-cached input token.</summary>
    public decimal Input { get; set; }

    /// <summary>Gets or sets the USD price per cached input token.</summary>
    public decimal CachedInput { get; set; }

    /// <summary>Gets or sets the USD price per output token.</summary>
    public decimal Output { get; set; }
}

public class TokenomicsSettings
{
    public int MonthlyTokenLimit { get; set; }=10000;
    public int DailyTokenLimit { get; set; }=1000;

    public decimal DailyBudgetUsd { get; set; } = 50m;
    public decimal MonthlyBudgetUsd { get; set; }=1000m;
    /// <summary>Gets or sets the delay duration in milliseconds.</summary>
    public int DelayDuration { get; set; } = (int)TimeSpan.FromSeconds(10).TotalMilliseconds;

    public double CurrentCapacityUtilizationPercent { get; set; }=0.0;
    public double CapacityConstraintThresholdPercent { get; set; }=80.0;

    public float HighQueueDepthThreshold { get; set; }=0.90f;

    public int LargeContextThreshold { get; set; }=2048;
    public int CriticalPriority { get; set; }=1;
    public int HighPriority { get; set; }=2;

    /// <summary>Gets or sets the default model name; an empty value leaves it unconfigured.</summary>
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>Gets or sets input, cached-input, and output USD prices per token for each priced model; unlisted models are excluded from spend totals.</summary>
    public Dictionary<string, ModelTokenPricing> ModelCostPerToken { get; set; } = new();
    /// <summary>Gets or sets model lists keyed by hierarchy prefix.</summary>
    public Dictionary< string, List<string>> ModelHierarchy { get; set; } = new();

    /// <summary>Gets or sets the action when abuse is detected.</summary>
    public TokenActionEnum AbuseDetectedAction { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the action for an exceeded monthly quota without an override or approved exception.</summary>
    public TokenActionEnum MonthlyQuotaExceededAction { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the action for an administrator override or approved exception.</summary>
    public TokenActionEnum AdministratorOverrideAction { get; set; } = TokenActionEnum.Bypass;

    /// <summary>Gets or sets the daily-quota action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum DailyQuotaGovernanceAction { get; set; } = TokenActionEnum.IncreaseLimit;

    /// <summary>Gets or sets the daily-quota action for other workloads.</summary>
    public TokenActionEnum DailyQuotaExceededAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the monthly-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum MonthlyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the monthly-budget action for other tenants.</summary>
    public TokenActionEnum MonthlyBudgetExceededAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the daily-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum DailyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the daily-budget action for other tenants.</summary>
    public TokenActionEnum DailyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;

    /// <summary>Gets or sets the capacity-constrained action for critical-priority or incident-response work.</summary>
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
    public TokenActionEnum DefaultAction { get; set; } = TokenActionEnum.None;


    
    private static readonly JsonSerializerOptions _serializerOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };


    /// <summary>Parses comma- or semicolon-separated key=value settings, preserving omitted defaults and returning false for invalid input.</summary>
    /// <remarks>DelayDuration accepts integer milliseconds or seconds with an ms or s suffix, or ss:fff and mm:ss:fff clock formats. DefaultModel uses a URI-escaped model name. Model prices use ModelCostPerToken.&lt;model&gt;=Input:&lt;price&gt;|CachedInput:&lt;price&gt;|Output:&lt;price&gt;; the legacy scalar value is treated as output pricing. Model hierarchies use ModelHierarchy=prefix: [model1, model2], prefix2: [model3]. Model names and hierarchy prefixes are URI-escaped.</remarks>
    public  bool TryParse(string data) {
        if (string.IsNullOrWhiteSpace(data)) {
            return false;
        }

        var parts = new List<string>();
        var partStart = 0;
        var hierarchyBracketDepth = 0;
        var isModelHierarchyPart = false;

        for (var index = 0; index < data.Length; index++) {
            var currentCharacter = data[index];

            if (!isModelHierarchyPart && currentCharacter == '=') {
                isModelHierarchyPart = data[partStart..index].Trim()
                    .Equals(nameof(ModelHierarchy), StringComparison.OrdinalIgnoreCase);
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

        if (parts.Count == 0) {
            return false;
        }

        var modelCostPrefix = nameof(ModelCostPerToken) + ".";

        foreach (var part in parts) {
            var splitIndex = part.IndexOf('=');
            if (splitIndex <= 0) {
                return false;
            }

            var (key, value) = ConfigParser.KVStringPairs([part]).Single();

            if (key.StartsWith(modelCostPrefix, StringComparison.OrdinalIgnoreCase)) {
                var model = Uri.UnescapeDataString(key[modelCostPrefix.Length..]);
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
                continue;
            }

            var property = typeof(TokenomicsSettings).GetProperty(key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property?.SetMethod is null) {
                return false;
            }

            object parsedValue;
            if (property.Name == nameof(ModelHierarchy)) {
                var hierarchy = new Dictionary<string, List<string>>();
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

                parsedValue = hierarchy;
            } else if (property.Name == nameof(DelayDuration)) {
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

                parsedValue = (int)totalMilliseconds;
            } else if (property.PropertyType == typeof(string)) {
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

            property.SetValue(this, parsedValue);
        }

        return true;
    }

    /// <summary>Returns semicolon-separated key=value settings with a clock-formatted delay, URI-escaped model names, model hierarchies, named actions, and invariant-culture numbers.</summary>
    public override string ToString() {
        var parts = new List<string>();
        foreach (var property in JsonSerializer.SerializeToElement(this, _serializerOptions).EnumerateObject()) {
            if (property.Name == nameof(DefaultModel)) {
                parts.Add($"{property.Name}={Uri.EscapeDataString(DefaultModel)}");
            } else if (property.Name == nameof(DelayDuration)) {
                if (DelayDuration < 0) {
                    throw new InvalidOperationException($"{nameof(DelayDuration)} cannot be negative.");
                }

                var totalMinutes = DelayDuration / 60000;
                var seconds = DelayDuration / 1000 % 60;
                var milliseconds = DelayDuration % 1000;
                var formattedDuration = totalMinutes > 0
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"{totalMinutes:D2}:{seconds:D2}:{milliseconds:D3}")
                    : string.Create(CultureInfo.InvariantCulture, $"{seconds:D2}:{milliseconds:D3}");
                parts.Add($"{property.Name}={formattedDuration}");
            } else if (property.Name == nameof(ModelCostPerToken)) {
                foreach (var modelCost in property.Value.EnumerateObject()) {
                    if (!ModelCostPerToken.TryGetValue(modelCost.Name, out var pricing) ||
                        pricing.Input < 0 ||
                        pricing.CachedInput < 0 ||
                        pricing.Output < 0) {
                        throw new InvalidOperationException(
                            $"{nameof(ModelCostPerToken)} values cannot be null or negative.");
                    }

                    parts.Add(
                        $"{property.Name}.{Uri.EscapeDataString(modelCost.Name)}=" +
                        $"{nameof(ModelTokenPricing.Input)}:{pricing.Input.ToString(CultureInfo.InvariantCulture)}|" +
                        $"{nameof(ModelTokenPricing.CachedInput)}:{pricing.CachedInput.ToString(CultureInfo.InvariantCulture)}|" +
                        $"{nameof(ModelTokenPricing.Output)}:{pricing.Output.ToString(CultureInfo.InvariantCulture)}");
                }
            } else if (property.Name == nameof(ModelHierarchy)) {
                var hierarchy = string.Join(", ", ModelHierarchy.Select(entry =>
                    $"{Uri.EscapeDataString(entry.Key)}: [{string.Join(", ", entry.Value.Select(Uri.EscapeDataString))}]"));
                parts.Add($"{property.Name}={hierarchy}");
            } else {
                parts.Add($"{property.Name}={property.Value}");
            }
        }

        return string.Join("; ", parts);
    }
}
