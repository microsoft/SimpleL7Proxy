using System.Text.Json.Serialization;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>
/// Combined tokenomics metrics returned for a user and model combination.
/// </summary>
public sealed class MetricsLookupResponse {
    /// <summary>User identifier associated with the metrics.</summary>
    [JsonPropertyName("UserId")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Model associated with the metrics.</summary>
    [JsonPropertyName("Model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>Input and output tokens consumed during the current UTC day.</summary>
    [JsonPropertyName("DailyTokenBalance")]
    public long DailyTokenBalance { get; set; }

    /// <summary>Input and output tokens consumed during the current UTC month.</summary>
    [JsonPropertyName("MonthlyTokenBalance")]
    public long MonthlyTokenBalance { get; set; }

    /// <summary>USD spend during the current UTC day.</summary>
    [JsonPropertyName("DailyBudgetUsage")]
    public decimal DailyBudgetUsage { get; set; }

    /// <summary>USD spend during the current UTC month.</summary>
    [JsonPropertyName("MonthlyBudgetUsage")]
    public decimal MonthlyBudgetUsage { get; set; }

    /// <summary>Whether recent activity is classified as abusive.</summary>
    [JsonPropertyName("IsAbuseDetected")]
    public bool IsAbuseDetected { get; set; }

    /// <summary>Whether an approved policy exception is active.</summary>
    [JsonPropertyName("HasApprovedException")]
    public bool HasApprovedException { get; set; }

    /// <summary>Whether an administrator override is active.</summary>
    [JsonPropertyName("HasAdministratorOverride")]
    public bool HasAdministratorOverride { get; set; }
}
