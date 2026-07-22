    /// <summary>The current onboarding step: home, server, model, model-select, path, prompt, parameters, include-usage, send, raw-exchange, eventhub, insights, or complete.</summary>
namespace chat_tester.Components.Shared;

/// <summary>
/// Per-user preferences: the override values a user has chosen across the UI that take
/// precedence over the defaults from the configuration file (<see cref="ChatTesterOptions"/>).
/// <para>
/// Intended to be serialized (JSON) and persisted per user, keyed by <see cref="UserId"/>.
/// A scalar left <c>null</c> (or an empty collection) means "no override" and the corresponding
/// default from the configuration file is used instead. This mirrors the shared settings
/// singletons (auth, user identity, headers, debug, layout, storage) so it can round-trip
/// between a stored document and the live settings.
/// </para>
/// </summary>
public sealed class UserPreferences
{
    /// <summary>Current schema version, incremented when the shape changes in a breaking way.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Identity of the user these preferences belong to. Used as the storage key.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Schema version of this stored document.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Timestamp of the last time these preferences were saved.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Overrides for the target server. Null when the server URL is unchanged.</summary>
    public ServerPreferences? Server { get; set; }

    /// <summary>Overrides for the User identity tab. Null when nothing was changed.</summary>
    public UserIdentityPreferences? UserIdentity { get; set; }

    /// <summary>Overrides for request composition and model/API selection. Null when unchanged.</summary>
    public RequestPreferences? Request { get; set; }

    /// <summary>Custom request headers. Null when the header list matches the defaults.</summary>
    public List<CustomHeaderPreference>? CustomHeaders { get; set; }

    /// <summary>Overrides for layout and response display. Null when unchanged.</summary>
    public DisplayPreferences? Display { get; set; }

    /// <summary>Progress through the first-use onboarding flow. Null when onboarding has not started.</summary>
    public OnboardingPreferences? Onboarding { get; set; }

    /// <summary>Overrides for where history and conversations are stored. Null when unchanged.</summary>
    public StoragePreferences? Storage { get; set; }
}

/// <summary>First-use onboarding progress persisted in the browser session cookie.</summary>
public sealed class OnboardingPreferences
{
    /// <summary>The current onboarding step: home, server, model, model-select, path, prompt, send, raw-exchange, or complete.</summary>
    public string CurrentStep { get; set; } = "home";
}

/// <summary>Server target overrides.</summary>
public sealed class ServerPreferences
{
    /// <summary>Overrides <see cref="ChatTesterOptions.ServerBaseUrl"/> when set.</summary>
    public string? ServerBaseUrl { get; set; }
}

/// <summary>User-identity header overrides mirroring <see cref="UserSettings"/>.</summary>
public sealed class UserIdentityPreferences
{
    /// <summary>Overrides <see cref="ChatTesterOptions.UserHeaderName"/> when set.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Overrides <see cref="ChatTesterOptions.PriorityKeyHeader"/> when set.</summary>
    public string? PriorityHeaderName { get; set; }

    /// <summary>"None", "Selected", "Random", or "Rotating".</summary>
    public string? SelectionMode { get; set; }

    /// <summary>The chosen user when <see cref="SelectionMode"/> is "Selected".</summary>
    public string? SelectedUser { get; set; }

    /// <summary>Newline-separated candidate users, each optionally "name, priority".</summary>
    public string? UserListText { get; set; }
}

/// <summary>Request-composition and model-selection overrides used across the pages.</summary>
public sealed class RequestPreferences
{
    /// <summary>Overrides <see cref="ChatTesterOptions.DefaultMethod"/> when set.</summary>
    public string? Method { get; set; }

    /// <summary>Overrides <see cref="ChatTesterOptions.ChatEndpointPath"/> when set.</summary>
    public string? EndpointPath { get; set; }

    /// <summary>Overrides <see cref="ChatTesterOptions.ChatRequestBody"/> when set.</summary>
    public string? RequestBody { get; set; }

    /// <summary>Request <c>Content-Type</c> chosen in the UI.</summary>
    public string? ContentType { get; set; }

    /// <summary>Overrides <see cref="ChatTesterOptions.RequestCount"/> when set.</summary>
    public int? RequestCount { get; set; }

    /// <summary>The model id selected in the model/API picker.</summary>
    public string? SelectedModel { get; set; }

    /// <summary>The API id selected in the model/API picker.</summary>
    public string? SelectedApi { get; set; }

    /// <summary>The current model filter text.</summary>
    public string? ModelFilter { get; set; }

    /// <summary>Whether the <c>S7PDEBUG</c> header is sent (see <see cref="RequestDebugSettings"/>).</summary>
    public bool? DebugEnabled { get; set; }
}

/// <summary>A single custom request header override (mirrors <see cref="HeaderSettings.HeaderItem"/>).</summary>
public sealed class CustomHeaderPreference
{
    /// <summary>Header name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Header value. May contain the <c>{id}</c> token for the request number.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>Layout and response-display overrides.</summary>
public sealed class DisplayPreferences
{
    /// <summary>Whether panels auto-collapse (see <see cref="AutoCollapseSettings"/>).</summary>
    public bool? AutoCollapse { get; set; }

    /// <summary>Preferred response rendering format (e.g. "Text", "Markdown", "Html").</summary>
    public string? ResponseFormat { get; set; }
}

/// <summary>
/// Storage-location overrides. A <c>null</c> group means "use the default storage from the
/// configuration file". Reuses the same shapes as the history and conversation stores.
/// </summary>
public sealed class StoragePreferences
{
    /// <summary>Overrides <see cref="ChatTesterOptions.History"/> when set.</summary>
    public HistoryStorageSettings? History { get; set; }

    /// <summary>Overrides <see cref="ChatTesterOptions.Conversations"/> when set.</summary>
    public ConversationStorageSettings? Conversations { get; set; }
}
