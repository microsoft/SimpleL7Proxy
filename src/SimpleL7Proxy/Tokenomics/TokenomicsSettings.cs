using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Tokenomics;

public class TokenomicsSettings
{
    private static readonly JsonSerializerOptions _serializerOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public int MonthlyTokenLimit { get; set; }=10000;
    public int DailyTokenLimit { get; set; }=1000;

    public decimal DailyBudgetUsd { get; set; } = 50m;
    public decimal MonthlyBudgetUsd { get; set; }=1000m;

    public double CurrentCapacityUtilizationPercent { get; set; }=0.0;
    public double CapacityConstraintThresholdPercent { get; set; }=80.0;

    public float HighQueueDepthThreshold { get; set; }=0.90f;

    public int LargeContextThreshold { get; set; }=2048;
    public int CriticalPriority { get; set; }=1;
    public int HighPriority { get; set; }=2;

    /// <summary>Gets or sets USD per input or output token for each priced model; unlisted models are excluded from spend totals.</summary>
    public Dictionary<string, decimal> ModelCostPerToken { get; set; } = new();

    /// <summary>Gets or sets the action when abuse is detected.</summary>
    public TokenActionEnum AbuseDetectedAction { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the action for an exceeded monthly quota without an override or approved exception.</summary>
    public TokenActionEnum MonthlyQuotaExceededAction { get; set; } = TokenActionEnum.Reject;

    /// <summary>Gets or sets the action for an administrator override or approved exception.</summary>
    public TokenActionEnum AdministratorOverrideAction { get; set; } = TokenActionEnum.Bypass;

    /// <summary>Gets or sets the daily-quota action for incident response, audit investigation, or compliance work.</summary>
    public TokenActionEnum DailyQuotaGovernanceAction { get; set; } = TokenActionEnum.IncreaseLimit;

    /// <summary>Gets or sets the daily-quota action for other workloads.</summary>
    public TokenActionEnum DailyQuotaExceededAction { get; set; } = TokenActionEnum.WaitForReset;

    /// <summary>Gets or sets the monthly-budget action for premium or enterprise tenants.</summary>
    public TokenActionEnum MonthlyBudgetEntitledTenantAction { get; set; } = TokenActionEnum.DowngradeModel;

    /// <summary>Gets or sets the monthly-budget action for other tenants.</summary>
    public TokenActionEnum MonthlyBudgetExceededAction { get; set; } = TokenActionEnum.DecreaseLimit;

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

    /// <summary>Parses comma- or semicolon-separated key=value settings, preserving omitted defaults and returning null for invalid input.</summary>
    /// <remarks>Model prices use ModelCostPerToken.&lt;model&gt;=price, with URI-escaped model names.</remarks>
    public static TokenomicsSettings? TryParse(string data) {
        if (string.IsNullOrWhiteSpace(data)) {
            return null;
        }

        var parts = data.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) {
            return null;
        }

        var settings = new TokenomicsSettings();
        var modelCostPrefix = nameof(ModelCostPerToken) + ".";

        foreach (var part in parts) {
            var splitIndex = part.IndexOf('=');
            if (splitIndex <= 0 || splitIndex >= part.Length - 1) {
                return null;
            }

            var (key, value) = ConfigParser.KVStringPairs([part]).Single();

            if (key.StartsWith(modelCostPrefix, StringComparison.OrdinalIgnoreCase)) {
                var model = Uri.UnescapeDataString(key[modelCostPrefix.Length..]);
                if (string.IsNullOrWhiteSpace(model) ||
                    !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var modelCost) ||
                    modelCost < 0) {
                    return null;
                }

                settings.ModelCostPerToken[model] = modelCost;
                continue;
            }

            var property = typeof(TokenomicsSettings).GetProperty(key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property?.SetMethod is null) {
                return null;
            }

            object parsedValue;
            if (property.PropertyType == typeof(int) &&
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
            } else if (property.PropertyType == typeof(TokenActionEnum) &&
                Enum.TryParse<TokenActionEnum>(value, true, out var action) && Enum.IsDefined(action) &&
                value.Equals(action.ToString(), StringComparison.OrdinalIgnoreCase)) {
                parsedValue = action;
            } else {
                return null;
            }

            property.SetValue(settings, parsedValue);
        }

        return settings;
    }

    /// <summary>Returns semicolon-separated key=value settings with named actions and invariant-culture numbers.</summary>
    public override string ToString() {
        var parts = new List<string>();
        foreach (var property in JsonSerializer.SerializeToElement(this, _serializerOptions).EnumerateObject()) {
            if (property.Name == nameof(ModelCostPerToken)) {
                foreach (var modelCost in property.Value.EnumerateObject()) {
                    parts.Add($"{property.Name}.{Uri.EscapeDataString(modelCost.Name)}={modelCost.Value}");
                }
            } else {
                parts.Add($"{property.Name}={property.Value}");
            }
        }

        return string.Join("; ", parts);
    }
}
