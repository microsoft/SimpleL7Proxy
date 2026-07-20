using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace chat_tester.Components.Shared;

/// <summary>
/// Per-session bridge between the live settings singletons and a base64-encoded JSON
/// <see cref="UserPreferences"/> blob persisted in a browser session cookie.
/// <para>
/// On <see cref="LoadAsync"/> the cookie is decoded and applied to the settings so the user's
/// overrides are restored. <see cref="SaveAsync"/> stores only the values that differ from the
/// configuration-file defaults (unchanged values are omitted); call it whenever an override
/// changes so preferences follow the user throughout the site.
/// </para>
/// <remarks>
/// Authorization values are intentionally NOT written to the cookie, since a JS-readable cookie
/// is an unsafe place to store credentials or auth configuration.
/// </remarks>
/// </summary>
public sealed class UserPreferencesService
{
    private const string CookieName = "chat_tester_prefs";
    private const string JsGet = "userPreferences.get";
    private const string JsSet = "userPreferences.set";
    private const string JsClear = "userPreferences.clear";
    private const string DefaultSelectionMode = "None";
    private const bool DefaultDebugEnabled = false;
    private const bool DefaultAutoCollapse = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IJSRuntime _js;
    private readonly ChatTesterOptions _defaults;
    private readonly AuthTokenSettings _auth;
    private readonly UserSettings _user;
    private readonly HeaderSettings _headers;
    private readonly RequestDebugSettings _debug;
    private readonly AutoCollapseSettings _autoCollapse;
    private readonly HistorySettings _history;
    private readonly ConversationSettings _conversations;

    private bool _loaded;

    public UserPreferencesService(
        IJSRuntime js,
        IOptions<ChatTesterOptions> defaults,
        AuthTokenSettings auth,
        UserSettings user,
        HeaderSettings headers,
        RequestDebugSettings debug,
        AutoCollapseSettings autoCollapse,
        HistorySettings history,
        ConversationSettings conversations)
    {
        _js = js;
        _defaults = defaults.Value;
        _auth = auth;
        _user = user;
        _headers = headers;
        _debug = debug;
        _autoCollapse = autoCollapse;
        _history = history;
        _conversations = conversations;
    }

    /// <summary>The current preferences snapshot (updated on load/save).</summary>
    public UserPreferences Preferences { get; private set; } = new();

    /// <summary>Returns whether the active onboarding step matches <paramref name="step"/>.</summary>
    public bool IsOnboardingStep(string step) =>
        string.Equals(Preferences.Onboarding?.CurrentStep ?? "home", step, StringComparison.Ordinal);

    /// <summary>Updates and persists the active first-use onboarding step.</summary>
    public async Task SetOnboardingStepAsync(string step)
    {
        Preferences.Onboarding = new OnboardingPreferences { CurrentStep = step };
        await SaveAsync();
        await PublishOnboardingStepAsync(step);
    }

    /// <summary>
    /// Page-owned request selections (model/API/path/content-type/filter/count) that are not backed
    /// by a settings singleton. Pages set only the fields the user changed (null = default) and call
    /// <see cref="SaveAsync"/>; these are preserved across saves and restored on load.
    /// </summary>
    public RequestPreferences RequestSelections { get; private set; } = new();

    /// <summary>
    /// Loads preferences from the session cookie and applies them to the settings singletons.
    /// Idempotent: only the first call per session reads the cookie.
    /// </summary>
    public async Task<bool> LoadAsync()
    {
        if (_loaded)
        {
            return false;
        }

        _loaded = true;

        string? cookie;
        try
        {
            cookie = await _js.InvokeAsync<string?>(JsGet, CookieName);
        }
        catch (JSException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        var preferences = TryDecode(cookie);
        if (preferences is null)
        {
            return false;
        }

        Preferences = preferences;
        RequestSelections = ClonePageRequest(preferences.Request);
        ApplyToSettings(preferences);
        await PublishOnboardingStepAsync(Preferences.Onboarding?.CurrentStep ?? "home");
        return true;
    }

    /// <summary>Writes the values that differ from the configuration defaults to the session cookie.</summary>
    public async Task SaveAsync()
    {
        var preferences = BuildDiff();
        Preferences = preferences;

        var json = JsonSerializer.Serialize(preferences, JsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        try
        {
            await _js.InvokeVoidAsync(JsSet, CookieName, encoded);
        }
        catch (JSException)
        {
            // Cookie storage is best-effort; ignore when JS is unavailable (e.g. prerendering).
        }
    }

    /// <summary>
    /// Returns the current preferences snapshot as JSON (indented for editing in the UI).
    /// </summary>
    public string ExportJson()
    {
        var snapshot = BuildDiff();
        Preferences = snapshot;

        var options = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true
        };

        return JsonSerializer.Serialize(snapshot, options);
    }

    /// <summary>
    /// Applies preferences from a JSON document, updates the live settings singletons,
    /// and persists the resulting preferences back to the session cookie.
    /// </summary>
    public async Task<(bool Success, string Error)> ImportJsonAsync(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (false, "Preferences JSON is empty.");
        }

        UserPreferences? preferences;
        try
        {
            preferences = JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}");
        }

        if (preferences is null)
        {
            return (false, "Preferences JSON could not be parsed.");
        }

        Preferences = preferences;
        RequestSelections = ClonePageRequest(preferences.Request);
        ApplyToSettings(preferences);
        await SaveAsync();
        return (true, string.Empty);
    }

    /// <summary>
    /// Clears stored preferences and reverts live settings to configuration defaults.
    /// </summary>
    public async Task ResetAsync()
    {
        _auth.ServerBaseUrl = _defaults.ServerBaseUrl;

        _user.HeaderName = _defaults.UserHeaderName;
        _user.PriorityHeaderName = _defaults.PriorityKeyHeader;
        _user.SelectionMode = DefaultSelectionMode;
        _user.SelectedUser = string.Empty;
        _user.UserListText = DefaultUserListText();

        _headers.Headers.Clear();
        foreach (var header in ParseDefaultHeaders(_defaults.DefaultHeaders))
        {
            _headers.Headers.Add(header);
        }

        _debug.DebugEnabled = DefaultDebugEnabled;
        _autoCollapse.Enabled = DefaultAutoCollapse;

        _history.Apply(_defaults.History);
        _conversations.Apply(_defaults.Conversations);

        Preferences = new UserPreferences
        {
            SchemaVersion = UserPreferences.CurrentSchemaVersion,
            UpdatedAt = DateTimeOffset.Now
        };
        RequestSelections = new RequestPreferences();

        try
        {
            await _js.InvokeVoidAsync(JsClear, CookieName);
        }
        catch (JSException)
        {
            // Clearing is best-effort when JS is unavailable.
        }
    }

    private static UserPreferences? TryDecode(string encoded)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            return JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task PublishOnboardingStepAsync(string step)
    {
        try
        {
            await _js.InvokeVoidAsync("userPreferences.setOnboardingStep", step);
        }
        catch (JSException)
        {
            // Browser notifications are best-effort when JS is unavailable (e.g. prerendering).
        }
    }

    private UserPreferences BuildDiff()
    {
        var preferences = new UserPreferences
        {
            SchemaVersion = UserPreferences.CurrentSchemaVersion,
            UpdatedAt = DateTimeOffset.Now
        };

        if (Differs(_auth.ServerBaseUrl, _defaults.ServerBaseUrl))
        {
            preferences.Server = new ServerPreferences { ServerBaseUrl = _auth.ServerBaseUrl };
        }

        // Authorization is intentionally never written to the cookie.

        var userIdentity = new UserIdentityPreferences();
        var hasUserIdentity = false;
        if (Differs(_user.HeaderName, _defaults.UserHeaderName))
        {
            userIdentity.HeaderName = _user.HeaderName;
            hasUserIdentity = true;
        }

        if (Differs(_user.PriorityHeaderName, _defaults.PriorityKeyHeader))
        {
            userIdentity.PriorityHeaderName = _user.PriorityHeaderName;
            hasUserIdentity = true;
        }

        if (Differs(_user.SelectionMode, DefaultSelectionMode))
        {
            userIdentity.SelectionMode = _user.SelectionMode;
            hasUserIdentity = true;
        }

        if (!string.IsNullOrEmpty(_user.SelectedUser))
        {
            userIdentity.SelectedUser = _user.SelectedUser;
            hasUserIdentity = true;
        }

        if (Differs(_user.UserListText, DefaultUserListText()))
        {
            userIdentity.UserListText = _user.UserListText;
            hasUserIdentity = true;
        }

        if (hasUserIdentity)
        {
            preferences.UserIdentity = userIdentity;
        }

        var request = ClonePageRequest(RequestSelections);
        if (_debug.DebugEnabled != DefaultDebugEnabled)
        {
            request.DebugEnabled = _debug.DebugEnabled;
        }

        if (HasRequestValues(request))
        {
            preferences.Request = request;
        }

        if (CustomHeadersDiffer())
        {
            preferences.CustomHeaders = _headers.Headers
                .Select(header => new CustomHeaderPreference { Name = header.Name, Value = header.Value })
                .ToList();
        }

        if (_autoCollapse.Enabled != DefaultAutoCollapse)
        {
            preferences.Display = new DisplayPreferences { AutoCollapse = _autoCollapse.Enabled };
        }

        if (Preferences.Onboarding is { CurrentStep.Length: > 0 } onboarding)
        {
            preferences.Onboarding = new OnboardingPreferences { CurrentStep = onboarding.CurrentStep };
        }

        StoragePreferences? storage = null;
        if (StorageDiffers(_history.Current, _defaults.History))
        {
            storage = new StoragePreferences { History = _history.Current };
        }

        if (StorageDiffers(_conversations.Current, _defaults.Conversations))
        {
            storage ??= new StoragePreferences();
            storage.Conversations = _conversations.Current;
        }

        preferences.Storage = storage;
        return preferences;
    }

    private string DefaultUserListText() =>
        _defaults.UserNames is { Length: > 0 } names ? string.Join(Environment.NewLine, names) : string.Empty;

    private bool CustomHeadersDiffer()
    {
        var current = _headers.Headers
            .Select(header => $"{header.Name}: {header.Value}")
            .ToList();

        var defaults = (_defaults.DefaultHeaders ?? Array.Empty<string>())
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (current.Count != defaults.Count)
        {
            return true;
        }

        return current.Where((line, index) => !string.Equals(line, defaults[index], StringComparison.Ordinal)).Any();
    }

    private static RequestPreferences ClonePageRequest(RequestPreferences? source) => new()
    {
        Method = source?.Method,
        EndpointPath = source?.EndpointPath,
        RequestBody = source?.RequestBody,
        ContentType = source?.ContentType,
        RequestCount = source?.RequestCount,
        SelectedModel = source?.SelectedModel,
        SelectedApi = source?.SelectedApi,
        ModelFilter = source?.ModelFilter
    };

    private static bool HasRequestValues(RequestPreferences request) =>
        request.DebugEnabled is not null
        || request.RequestCount is not null
        || !string.IsNullOrEmpty(request.Method)
        || !string.IsNullOrEmpty(request.EndpointPath)
        || !string.IsNullOrEmpty(request.RequestBody)
        || !string.IsNullOrEmpty(request.ContentType)
        || !string.IsNullOrEmpty(request.SelectedModel)
        || !string.IsNullOrEmpty(request.SelectedApi)
        || !string.IsNullOrEmpty(request.ModelFilter);

    private static bool Differs(string? current, string? @default) =>
        !string.Equals(current ?? string.Empty, @default ?? string.Empty, StringComparison.Ordinal);

    private static IEnumerable<HeaderSettings.HeaderItem> ParseDefaultHeaders(string[]? defaultHeaders)
    {
        if (defaultHeaders is not { Length: > 0 })
        {
            yield break;
        }

        foreach (var line in defaultHeaders)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                yield return new HeaderSettings.HeaderItem { Name = line.Trim(), Value = string.Empty };
                continue;
            }

            yield return new HeaderSettings.HeaderItem
            {
                Name = line[..separator].Trim(),
                Value = line[(separator + 1)..].Trim()
            };
        }
    }

    private static bool StorageDiffers(HistoryStorageSettings current, HistoryStorageSettings @default) =>
        !string.Equals(current.Mode, @default.Mode, StringComparison.Ordinal)
        || !string.Equals(current.DiskPath, @default.DiskPath, StringComparison.Ordinal)
        || !string.Equals(current.StorageAccountName, @default.StorageAccountName, StringComparison.Ordinal)
        || !string.Equals(current.BlobContainerName, @default.BlobContainerName, StringComparison.Ordinal)
        || !string.Equals(current.CosmosAccount, @default.CosmosAccount, StringComparison.Ordinal)
        || !string.Equals(current.CosmosDatabase, @default.CosmosDatabase, StringComparison.Ordinal)
        || !string.Equals(current.CosmosContainer, @default.CosmosContainer, StringComparison.Ordinal);

    private static bool StorageDiffers(ConversationStorageSettings current, ConversationStorageSettings @default) =>
        !string.Equals(current.Mode, @default.Mode, StringComparison.Ordinal)
        || !string.Equals(current.DiskPath, @default.DiskPath, StringComparison.Ordinal)
        || !string.Equals(current.StorageAccountName, @default.StorageAccountName, StringComparison.Ordinal)
        || !string.Equals(current.BlobContainerName, @default.BlobContainerName, StringComparison.Ordinal)
        || !string.Equals(current.CosmosAccount, @default.CosmosAccount, StringComparison.Ordinal)
        || !string.Equals(current.CosmosDatabase, @default.CosmosDatabase, StringComparison.Ordinal)
        || !string.Equals(current.CosmosContainer, @default.CosmosContainer, StringComparison.Ordinal);

    private void ApplyToSettings(UserPreferences preferences)
    {
        if (preferences.Server?.ServerBaseUrl is { } serverBaseUrl && !string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            _auth.ServerBaseUrl = serverBaseUrl;
        }

        var userIdentity = preferences.UserIdentity;
        if (userIdentity is not null)
        {
            ApplyIfPresent(userIdentity.HeaderName, value => _user.HeaderName = value);
            ApplyIfPresent(userIdentity.PriorityHeaderName, value => _user.PriorityHeaderName = value);
            ApplyIfPresent(userIdentity.SelectionMode, value => _user.SelectionMode = value);
            ApplyIfPresent(userIdentity.SelectedUser, value => _user.SelectedUser = value);
            ApplyIfPresent(userIdentity.UserListText, value => _user.UserListText = value);
        }

        if (preferences.Request?.DebugEnabled is { } debugEnabled)
        {
            _debug.DebugEnabled = debugEnabled;
        }

        if (preferences.CustomHeaders is { Count: > 0 } customHeaders)
        {
            _headers.Headers.Clear();
            foreach (var header in customHeaders)
            {
                _headers.Headers.Add(new HeaderSettings.HeaderItem { Name = header.Name, Value = header.Value });
            }
        }

        if (preferences.Display?.AutoCollapse is { } autoCollapse)
        {
            _autoCollapse.Enabled = autoCollapse;
        }

        if (preferences.Storage?.History is { } historyStorage)
        {
            _history.Apply(historyStorage);
        }

        if (preferences.Storage?.Conversations is { } conversationStorage)
        {
            _conversations.Apply(conversationStorage);
        }
    }

    private static void ApplyIfPresent(string? value, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value);
        }
    }
}
