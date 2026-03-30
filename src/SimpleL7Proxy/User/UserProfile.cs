using System;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using Microsoft.AspNetCore.Http;

namespace SimpleL7Proxy.User;

public class UserProfile : BackgroundService, IUserProfileService, IConfigChangeSubscriber
{
    private readonly ProxyConfig _options;

    private volatile Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private volatile List<string> suspendedUserProfiles = new List<string>();
    private volatile Dictionary<string, Dictionary<string, string>> authAppIDs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    // private DateTime? lastSuccessfulProfileLoad = null;
    // private TimeSpan? staleDuration = null;

    ConfigStatus profileConfigStatus = new ConfigStatus();
    ConfigStatus suspendedUserConfigStatus = new ConfigStatus()
    {
        zeroProfilesOK = true // It's valid to have zero suspended users, so we set this flag to avoid treating that as degraded state
    };
    ConfigStatus authAppIDsConfigStatus = new ConfigStatus();

    private volatile bool isInitialized = false;
    private static TimeSpan SoftDeleteExpirationPeriod;
    private static readonly HttpClient httpClient = new HttpClient();

    // Reusable ProxyEvent for profile error logging to reduce allocations
    private readonly ProxyEvent _profileErrorEvent = new ProxyEvent(4);  // Message, EntityId/ConfigUrl, EntityType, Timestamp
    private readonly object _profileErrorEventLock = new object();

    // Special keys used to mark deleted profiles in-place
    private const string s_DeletedAtKey = "__DeletedAt";
    private const string s_ExpiresAtKey = "__ExpiresAt";

    private readonly ILogger<UserProfile> _logger;
    private volatile Dictionary<string, AsyncClientInfo> _userInformation = new();

    private bool doUserConfig = false;
    private bool doSuspendedUserConfig = false;
    private bool doAuthAppIDConfig = false;

    private int NormalDelayMs;
    private const int s_ErrorDelayMs = 3000; // 3 seconds
    private bool _configRequired = false;

    public UserProfile(ProxyConfig options, ConfigChangeNotifier configChangeNotifier, ILogger<UserProfile> logger)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));
        ArgumentNullException.ThrowIfNull(configChangeNotifier, nameof(configChangeNotifier));

        _options = options;
        _logger = logger;
        _userInformation = CreateDefaultAsyncClientInfoCache();
        _logger.LogInformation("[INIT] UserProfile service starting.  Lookup header: {Header}, Config URL: {ConfigUrl}, Refresh interval: {RefreshInterval}s, Soft-delete TTL: {SoftDeleteTTL} minutes, Required: {Required}",
            options.UserIDFieldName, options.UserConfigUrl, options.UserConfigRefreshIntervalSecs, options.UserSoftDeleteTTLMinutes, options.UserConfigRequired);

        initVars();

        // Subscribe to config change notifications
        configChangeNotifier.Subscribe(this,
           [options => options.UseProfiles,
            options => options.UserConfigUrl,
            options => options.UserIDFieldName,
            options => options.SuspendedUserConfigUrl,
            options => options.ValidateAuthAppIDUrl,
            options => options.UserConfigRefreshIntervalSecs,
            options => options.UserSoftDeleteTTLMinutes,
            options => options.AsyncClientConfigFieldName]);
    }

    public void initVars()
    {

        doUserConfig = _options.UseProfiles && !string.IsNullOrEmpty(_options.UserConfigUrl);
        doSuspendedUserConfig = doUserConfig && !string.IsNullOrEmpty(_options.SuspendedUserConfigUrl);
        doAuthAppIDConfig = doUserConfig && !string.IsNullOrEmpty(_options.ValidateAuthAppIDUrl);
        SoftDeleteExpirationPeriod = TimeSpan.FromMinutes(_options.UserSoftDeleteTTLMinutes);

        NormalDelayMs = _options.UserConfigRefreshIntervalSecs * 1000; // Configurable interval
        _configRequired = _options.UserConfigRequired;
    }

    public Task OnConfigChangedAsync(
                IReadOnlyList<ConfigChange> changes,
                ProxyConfig backendOptions,
                CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received config change notification for UserProfile service. Changes: {Changes}",
            string.Join(", ", changes.Select(c => c.ToString())));

        initVars();

        if (!doUserConfig)
        {
            isInitialized = true;
        }
        return Task.CompletedTask;
    }


    public enum ParsingMode
    {
        ProfileMode,
        SuspendedUserMode,
        AuthAppIDMode
    }

    private void OnApplicationStopping()
    {
        _cancellationTokenSource?.Cancel();
    }

    CancellationTokenSource? _cancellationTokenSource;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("[SHUTDOWN] ⏹ User Profile Reader stopping");
        });

        // // Initialize User Profiles
        // if (!_options.UseProfiles || string.IsNullOrEmpty(_options.UserConfigUrl))
        // {
        //     _logger.LogInformation("User profiles disabled or no config URL provided. Skipping profile loading.");
        //     isInitialized = true; // Service is ready since profiles are not required
        //     return;
        // }

        // ConfigStatus profileConfigStatus = new();
        // ConfigStatus suspendedUserConfigStatus = new();
        // ConfigStatus authAppIDsConfigStatus = new();
        StringBuilder sb = new StringBuilder();

        while (!_cancellationTokenSource.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTime startTime = DateTime.UtcNow;
                sb.Clear();
                sb.Append("[PROFILE-READER] ");
                bool success = false;
                bool localIsInitialized = true;

                // Launch all enabled config reads concurrently (independent I/O)
                Task<CurrentRequestStatus>? profileTask = doUserConfig
                    ? ProfileReader(profileConfigStatus, _options.UserConfigUrl!, ParsingMode.ProfileMode)
                    : null;
                Task<CurrentRequestStatus>? suspendedTask = doSuspendedUserConfig
                    ? SuspendedUserReader(suspendedUserConfigStatus)
                    : null;
                Task<CurrentRequestStatus>? authTask = doAuthAppIDConfig
                    ? AuthReader(authAppIDsConfigStatus)
                    : null;

                // Process profile result
                if (profileTask != null)
                {
                    var profileStatus = await profileTask.ConfigureAwait(false);
                    if (!profileStatus.Success)
                    {
                        success = false;
                        if (profileConfigStatus.IsReallyStale)
                        {
                            var msg = $"Profile config has been really stale for {(DateTime.UtcNow - profileConfigStatus.LastSuccessfulLoad).TotalSeconds:F0}s";
                            _logger.LogError($"{msg}, exceeding the soft-delete expiration period of {SoftDeleteExpirationPeriod.TotalMinutes} minutes.");
                            localIsInitialized = false;

                            LogMsg(msg);
                        }

                        LogMsg($"Failed to load user profile config at { _options.UserConfigUrl ?? "null" }: {profileStatus.Message}");
                    }
                    else
                    {
                        success = true;
                    }

                    if (!profileConfigStatus.HasData)
                        localIsInitialized = false;
                    sb.Append($"(Profiles) {profileConfigStatus}");
                }

                if (suspendedTask != null)
                {
                    var suspendedStatus = await suspendedTask.ConfigureAwait(false);
                    success = success && suspendedStatus.Success;
                    sb.Append($", (Suspended) {suspendedUserConfigStatus}");
                }

                if (authTask != null)
                {
                    var authAppIDsStatus = await authTask.ConfigureAwait(false);
                    success = success && authAppIDsStatus.Success;

                    if (!authAppIDsStatus.Success)
                    {
                        if (authAppIDsConfigStatus.IsReallyStale)
                        {
                            var msg = $"Auth app ID config has been really stale for {(DateTime.UtcNow - authAppIDsConfigStatus.LastSuccessfulLoad).TotalSeconds:F0}s";
                            _logger.LogError($"{msg}, exceeding the soft-delete expiration period of {SoftDeleteExpirationPeriod.TotalMinutes} minutes.");
                            localIsInitialized = false;
                            LogMsg(msg);
                        }
                    }
                    
                    if (!authAppIDsConfigStatus.HasData)
                        localIsInitialized = false;

                    sb.Append($", (AuthAppIDs) {authAppIDsConfigStatus}");
                }

                if (sb.Length > 0)
                    _logger.LogInformation(sb.ToString());

                if (profileTask == null && suspendedTask == null && authTask == null)
                {
                    success = true; // No configs to load means we're ready by default
                }

                int baseDelay = success ? NormalDelayMs : s_ErrorDelayMs;
                int elapsedMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
                int remainingDelay = Math.Max(0, baseDelay - elapsedMs);

                // initilized if:  no errors && hasData  || users not required
                isInitialized = !_configRequired || localIsInitialized;
                // Console.WriteLine($"[PROFILE-DEBUG] Load {(success ? "succeeded" : "failed")}, Initialized: {isInitialized}, LocalIsInitialized: {localIsInitialized}");

                await Task.Delay(remainingDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogMsg("ConfigReader task failed", ex.Message);

                Console.Error.WriteLine($"FATAL: ConfigReader task failed: {ex.Message}");
            }
        }
    }

    private void LogMsg(string msg, string? excMsg = null)
    {
        lock (_profileErrorEventLock)
        {
            _profileErrorEvent.ClearEventData();
            _profileErrorEvent.Type = EventType.ProfileError;
            _profileErrorEvent["Message"] = msg;
            if (excMsg != null)
            {
                _profileErrorEvent["Exception"] = excMsg;
            }
            _profileErrorEvent.SendEvent();
        }
    }

    private class CurrentRequestStatus
    {
        public bool Success { get; set; } = false;
        public string? Message { get; set; } = null;
    }

    private class ConfigStatus
    {
        public DateTime LastSuccessfulLoad { get; set; } = DateTime.UtcNow;
        public bool IsStale { get; set; }
        public volatile bool IsReallyStale = false;
        public TimeSpan? StaleDuration { get; set; }
        public int TotalProfiles { get; set; }
        public int ActiveProfiles { get; set; }
        public int SoftDeletedProfiles { get; set; }
        public bool HasData => TotalProfiles > 0;
        public bool zeroProfilesOK = false;


        public void UpdateConfigStatusByEntries(Dictionary<string, Dictionary<string, string>> entries, bool lastSuccess)
        {
            UpdateConfigStatus(entries.Count, lastSuccess);

            int softDeletedCount = 0;
            foreach (var entry in entries.Values)
            {
                if (entry.ContainsKey(s_DeletedAtKey))
                    softDeletedCount++;
            }
            ActiveProfiles = entries.Count - softDeletedCount;
            SoftDeletedProfiles = softDeletedCount;
        }

        public void UpdateConfigStatus(int count, bool lastSuccess)
        {
            if (lastSuccess)
                LastSuccessfulLoad = DateTime.UtcNow;

            IsStale = !lastSuccess && count > 0;
            IsReallyStale = IsStale && DateTime.UtcNow - LastSuccessfulLoad > SoftDeleteExpirationPeriod;

            StaleDuration = IsStale && LastSuccessfulLoad != default
            ? DateTime.UtcNow - LastSuccessfulLoad
            : null;

            ActiveProfiles = TotalProfiles = count;
        }

        public override string ToString()
        {
            string prefix;

            if (IsStale && StaleDuration.HasValue)
            {
                prefix = $"STALE {StaleDuration.Value.TotalSeconds:F0}s";
            }
            else if ((StaleDuration.HasValue && StaleDuration.Value >= TimeSpan.FromMinutes(5)) ||
                    (!zeroProfilesOK && (TotalProfiles == 0 || ActiveProfiles == 0)))
            {
                prefix = "DEGRADED";
            }
            else
            {
                prefix = "HEALTHY";
            }

            return $"{prefix} - Tot: {TotalProfiles}, Act: {ActiveProfiles}, Soft-Del: {SoftDeletedProfiles} ";
        }
    }

    // private string DetailedMsg(bool staleProfilesExpired, bool hasProfileData, bool hasAuthData)
    // {
    //     if (staleProfilesExpired)
    //     {
    //         return $"Service not ready: stale profiles exceeded TTL ({staleDuration?.TotalMinutes:F1} minutes).";
    //     }

    //     if (!hasProfileData)
    //     {
    //         return "Service not ready: no profile data available.";
    //     }

    //     if (!hasAuthData)
    //     {
    //         return "Service not ready: no auth app ID data available.";
    //     }

    //     return "Service not ready: missing required data.";
    // }

    private void LogFailedLoads(CurrentRequestStatus profileStatus, CurrentRequestStatus suspendedStatus, CurrentRequestStatus authAppIDsStatus)
    {
        if (!profileStatus.Success)
        {
            _logger.LogError($"Load Failed: User profile: {_options.UserConfigUrl} - {profileStatus.Message}");
        }

        if (!suspendedStatus.Success)
        {
            _logger.LogError($"Load Failed: Suspended user config: {_options.SuspendedUserConfigUrl} - {suspendedStatus.Message}");
        }

        if (!authAppIDsStatus.Success)
        {
            _logger.LogError($"Load Failed: Auth app ID config: {_options.ValidateAuthAppIDUrl} - {authAppIDsStatus.Message}");
        }
    }

    // TODO  make all three the same

    private async Task<CurrentRequestStatus> ProfileReader(ConfigStatus status, string url, ParsingMode mode)
    {
        var callResult = await ReadUserConfigAsync(url ?? string.Empty, mode).ConfigureAwait(false);

        if (callResult.Success)
        {
            if (status.IsStale)
            {
                _logger.LogWarning($"RECOVERED loading configs: User profiles ({url})");
            }
        }
        else
        {
            if (userProfiles.Count > 0)
            {
                callResult.Message = $"Stale {status.StaleDuration?.TotalSeconds:F0}s";
            }
            else
            {
                callResult.Message = "No cached data available";
            }
        }

        status.UpdateConfigStatusByEntries(userProfiles, callResult.Success);

        return callResult;
    }

    private async Task<CurrentRequestStatus> SuspendedUserReader(ConfigStatus suspendedUserConfigStatus)
    {
        var suspendedStatus = await ReadUserConfigAsync(_options.SuspendedUserConfigUrl ?? string.Empty, ParsingMode.SuspendedUserMode).ConfigureAwait(false);

        if (!suspendedStatus.Success && !string.IsNullOrEmpty(_options.SuspendedUserConfigUrl))
        {
            suspendedStatus.Message = $"ERROR loading configs: Suspended users ({_options.SuspendedUserConfigUrl}) [{suspendedStatus.Message}]";
        }

        suspendedUserConfigStatus.UpdateConfigStatus(suspendedUserProfiles.Count, suspendedStatus.Success);

        return suspendedStatus;
    }

    private async Task<CurrentRequestStatus> AuthReader(ConfigStatus authAppIDsConfigStatus)
    {
        var authAppIDsStatus = await ReadUserConfigAsync(_options.ValidateAuthAppIDUrl ?? string.Empty, ParsingMode.AuthAppIDMode).ConfigureAwait(false);

        if (!authAppIDsStatus.Success && !string.IsNullOrEmpty(_options.ValidateAuthAppIDUrl))
        {
            authAppIDsStatus.Message = $"ERROR loading configs: AuthAppIDs ({_options.ValidateAuthAppIDUrl}) [{authAppIDsStatus.Message}]";
        }

        authAppIDsConfigStatus.UpdateConfigStatusByEntries(authAppIDs, authAppIDsStatus.Success);

        return authAppIDsStatus;
    }

    public bool ServiceIsReady()
    {
        return isInitialized;
    }

    private async Task<CurrentRequestStatus> ReadUserConfigAsync(string config, ParsingMode mode)
    {
        if (string.IsNullOrEmpty(config))
        {
            return new CurrentRequestStatus { Success = true, Message = null }; // Empty config is considered success (not required)
        }
        // Read user config from URL

        string fileContent = string.Empty;
        string location = config;

        if (location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // Location refers to a filepath
            var file = new FileInfo(location.Substring(5));
            if (file.Exists && file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                // Read from file
                try
                {
                    fileContent = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new CurrentRequestStatus { Success = false, Message = $"Error reading file: {ex.Message}" };
                }
            }
            else
            {
                return new CurrentRequestStatus { Success = false, Message = "File not found or not a JSON file" };
            }
        }
        else
        {
            // Read from URL
            try
            {
                fileContent = await httpClient.GetStringAsync(location).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                return new CurrentRequestStatus { Success = false, Message = $"HTTP error: {e.Message}" };
            }
        }

        if (!string.IsNullOrEmpty(fileContent))
        {
            return ParseUserConfig(fileContent, mode);
        }

        return new CurrentRequestStatus { Success = false, Message = "Empty file content" };
    }


    // Parse the user config JSON
    // Expected format:
    // [
    //     {
    //         "userId": "user1",
    //         "key1": "value1",
    //         "key2": "value2"
    //     },
    //     {
    //         "userId": "user2",
    //         "key1": "value1",
    //         "key2": "value2"
    //     }
    // ]

    private CurrentRequestStatus ParseUserConfig(string fileContent, ParsingMode mode)
    {

        var response = new CurrentRequestStatus { Success = true, Message = null };

        Dictionary<string, Dictionary<string, string>> localUserProfiles = new Dictionary<string, Dictionary<string, string>>();
        List<string> localSuspendedUserProfiles = new List<string>();
        Dictionary<string, Dictionary<string, string>> localAuthAppIDs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(fileContent))
        {
            response.Success = false;
            response.Message = "No user config provided to parse.";
            return response;
        }

        try
        {
            var userConfig = JsonSerializer.Deserialize<JsonElement>(fileContent ?? string.Empty);

            if (userConfig.ValueKind != JsonValueKind.Array)
            {
                response.Success = false;
                response.Message = "User config is not an array.";
                return response;
            }

            string lookupFieldName = "";
            if (mode == ParsingMode.SuspendedUserMode || mode == ParsingMode.ProfileMode)
            {
                lookupFieldName = _options.UserIDFieldName;
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                lookupFieldName = _options.ValidateAuthAppFieldName;
            }

            foreach (var profile in userConfig.EnumerateArray())
            {
                // Depending on the parsing mode, look up the appropriate field
                if (profile.TryGetProperty(lookupFieldName, out JsonElement entityElement))
                {
                    var entityId = entityElement.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(entityId))
                    {
                        if (mode == ParsingMode.SuspendedUserMode)
                        {
                            localSuspendedUserProfiles.Add(entityId);
                            continue;
                        }

                        if (mode == ParsingMode.AuthAppIDMode)
                        {
                            if (profile.TryGetProperty(_options.ValidateAuthAppFieldName, out JsonElement authAppIdElement))
                            {
                                string authAppId = authAppIdElement.GetString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(authAppId))
                                {
                                    Dictionary<string, string> authAppEntry = new Dictionary<string, string>();
                                    foreach (var property in profile.EnumerateObject())
                                    {
                                        if (!property.Name.Equals(_options.ValidateAuthAppFieldName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            if (property.Name == s_DeletedAtKey || property.Name == s_ExpiresAtKey)
                                            {
                                                _logger.LogWarning($"AuthAppID {authAppId} contains reserved key '{property.Name}' - removing key");
                                                continue;
                                            }
                                            authAppEntry[property.Name] = property.Value.ToString();
                                        }
                                    }
                                    localAuthAppIDs[authAppId] = authAppEntry;
                                }
                            }
                            continue;
                        }

                        if (mode == ParsingMode.ProfileMode)
                        {
                            Dictionary<string, string> kvPairs = new Dictionary<string, string>();

                            foreach (var property in profile.EnumerateObject())
                            {
                                if (!property.Name.Equals(_options.UserIDFieldName, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (property.Name == s_DeletedAtKey || property.Name == s_ExpiresAtKey)
                                    {
                                        _logger.LogWarning($"Profile {entityId} contains reserved key '{property.Name}' - removing key");
                                        continue;
                                    }
                                    kvPairs[property.Name] = property.Value.ToString();
                                }
                            }

                            localUserProfiles[entityId] = kvPairs;
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Profile field is missing {_options.UserIDFieldName}. Skipping...");
                    }
                }
            }

            if (mode == ParsingMode.SuspendedUserMode)
            {
                // no suspended users is not an error - it just means no users are suspended, so we treat it as success but log a warning
                if (localSuspendedUserProfiles.Count == 0)
                {
                    // response.Success = false;
                    // response.Message = "No valid suspended user profiles found in config.";
                }
                Interlocked.Exchange(ref suspendedUserProfiles, localSuspendedUserProfiles);
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                var (result, _, _, _) = ApplySoftDeletes(authAppIDs, localAuthAppIDs, "AuthAppID");

                if (result.Count == 0)
                {
                    response.Success = false;
                    response.Message = "No valid auth app IDs found in config.";
                }
                Interlocked.Exchange(ref authAppIDs, result);
            }
            else if (mode == ParsingMode.ProfileMode)
            {
                var (result, _, _, _) = ApplySoftDeletes(userProfiles, localUserProfiles, "Profile");

                if (result.Count == 0)
                {
                    response.Success = false;
                    response.Message = "No valid user profiles found in config.";
                }
                Interlocked.Exchange(ref userProfiles, result);
                Interlocked.Exchange(ref _userInformation, CreateDefaultAsyncClientInfoCache());
            }
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.Message = $"Error parsing user config: {ex.Message}";
        }

        return response;
    }

    public (Dictionary<string, string> profile, bool isSoftDeleted, bool isStale) GetUserProfile(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return (new Dictionary<string, string>(), false, false);
        }

        // Capture snapshots for thread safety
        var currentProfiles = userProfiles;
        var currentStale = profileConfigStatus.IsReallyStale;

        // Check if profile exists
        if (currentProfiles.TryGetValue(userId, out var profile))
        {
            var (isValid, isSoftDeleted) = CheckSoftDeleteStatus(profile, userId, "Profile");

            if (!isValid)
            {
                // Expired - treat as not found
                return (new Dictionary<string, string>(), false, false);
            }

            if (isSoftDeleted || profile.ContainsKey(s_DeletedAtKey))
            {
                // Return profile without deletion markers
                return (CleanSoftDeleteMarkers(profile), isSoftDeleted, currentStale);
            }

            // Active profile (not marked as deleted) - return direct reference for performance
            // IMPORTANT: Callers must not modify the returned dictionary
            return (profile, false, currentStale);
        }

        return (new Dictionary<string, string>(), false, false);
    }

    public bool IsUserSuspended(string userId)
    {
        return suspendedUserProfiles.Contains(userId);
    }
    public bool IsAuthAppIDValid(string? authAppId)
    {
        if (string.IsNullOrEmpty(authAppId))
        {
            return false;
        }

        // Capture snapshot for thread safety
        var currentAuthAppIDs = authAppIDs;

        // Check if the authAppId exists and is valid (not expired)
        if (currentAuthAppIDs.TryGetValue(authAppId, out var entry))
        {
            var (isValid, _) = CheckSoftDeleteStatus(entry, authAppId, "AuthAppID");
            return isValid;
        }

        return false;
    }

    public bool IsUserDeleted(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var currentProfiles = userProfiles;

        if (!currentProfiles.TryGetValue(userId, out var profile))
        {
            return false;
        }

        var (isValid, isSoftDeleted) = CheckSoftDeleteStatus(profile, userId, "Profile");
        return isValid && isSoftDeleted;
    }

    /// <summary>
    /// Checks the soft-delete status of an entry with deletion markers.
    /// Returns whether the entry is valid (active or within grace period) and if it's soft-deleted.
    /// </summary>
    /// <param name="entry">The dictionary entry to check</param>
    /// <param name="entityId">The ID of the entity for logging</param>
    /// <param name="entityType">The type of entity (e.g., "Profile", "AuthAppID") for logging</param>
    /// <returns>(isValid, isSoftDeleted) - isValid means entry can be used, isSoftDeleted indicates grace period</returns>
    private (bool isValid, bool isSoftDeleted) CheckSoftDeleteStatus(Dictionary<string, string> entry, string entityId, string entityType)
    {
        // Fast path: Check s_ExpiresAtKey first with TryGetValue (single lookup instead of ContainsKey + indexer)
        // Most entries won't have deletion markers, so this is the common case
        if (!entry.TryGetValue(s_ExpiresAtKey, out var expiresAtStr))
        {
            // No expiration marker - active entry
            return (true, false);
        }

        // Has s_ExpiresAtKey - verify s_DeletedAtKey also exists for consistency
        if (!entry.ContainsKey(s_DeletedAtKey))
        {
            // Inconsistent state (has ExpiresAt but no DeletedAt) - treat as active
            return (true, false);
        }

        // Both markers present - check if deletion has expired
        if (DateTime.TryParse(expiresAtStr, out var expiresAt))
        {
            // Within grace period: valid but soft-deleted; Expired: no longer valid
            return expiresAt > DateTime.UtcNow ? (true, true) : (false, false);
        }

        // Failed to parse expiration timestamp - log error but allow entry through
        // DEFENSIVE: Prefer false positive (allow corrupted soft-delete) over false negative (block legitimate entity)
        lock (_profileErrorEventLock)
        {
            _profileErrorEvent.ClearEventData();
            _profileErrorEvent.Type = EventType.ProfileError;
            _profileErrorEvent["Message"] = $"Failed to parse deletion timestamp for {entityType} - treating as active";
            _profileErrorEvent["EntityId"] = entityId;
            _profileErrorEvent["EntityType"] = entityType;
            _profileErrorEvent["Timestamp"] = expiresAtStr;
            _profileErrorEvent.SendEvent();
        }
        return (true, false);
    }

    public AsyncClientInfo? GetAsyncParams(string userId)
    {
        var currentUserInformation = _userInformation;

        // Check cache first (thread-safe)
        if (currentUserInformation.TryGetValue(userId, out var cachedInfo))
        {
            return cachedInfo;
        }

        // Capture snapshot for thread safety
        var currentProfiles = userProfiles;

        if (!currentProfiles.TryGetValue(userId, out var data))
        {
            _logger.LogWarning($"User profile: profile for user {userId} not found.");
            return null;
        }

        // Check soft-delete status - don't allow async for deleted profiles
        var (isValid, isSoftDeleted) = CheckSoftDeleteStatus(data, userId, "Profile");
        if (!isValid)
        {
            _logger.LogWarning($"User profile: profile for user {userId} has expired.");
            return null;
        }
        if (isSoftDeleted)
        {
            _logger.LogWarning($"User profile: profile for user {userId} is soft-deleted.");
            return null;
        }

        // Check if async config field exists
        // Format: enabled=true, containername=user123456, topic=status-123456, timeout=3600
        if (!data.TryGetValue(_options.AsyncClientConfigFieldName, out var asyncConfig))
        {
            _logger.LogWarning($"User profile: async config not found for user {userId}.");
            return null;
        }

        // Parse async config string into key-value pairs
        var asyncConfigParts = asyncConfig
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Select(part =>
            {
                var split = part.Split('=', 2);
                return split.Length == 2
                    ? new KeyValuePair<string, string>(split[0].Trim(), split[1].Trim())
                    : new KeyValuePair<string, string>(string.Empty, string.Empty);
            })
            .Where(kv => !string.IsNullOrEmpty(kv.Key))
            .ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value);

        // Validate enabled field first (required)
        if (!asyncConfigParts.TryGetValue("enabled", out var enabledValue) ||
            !bool.TryParse(enabledValue, out var asyncEnabled) || !asyncEnabled)
        {
            _logger.LogWarning($"User profile: async mode not enabled for user {userId}.");
            return null;
        }

        // Parse and validate container name (required)
        string containerName = string.Empty;
        if (asyncConfigParts.TryGetValue("containername", out var containerValue))
        {
            if (!IsValidBlobContainerName(containerValue, out containerName))
            {
                _logger.LogWarning($"User profile: invalid blob container name for user {userId}: {containerValue}.");
                return null;
            }
        }

        // Parse and validate topic name (required)
        string topicName = string.Empty;
        if (asyncConfigParts.TryGetValue("topic", out var topicValue))
        {
            if (!IsValidServiceBusTopicName(topicValue, out topicName))
            {
                _logger.LogWarning($"User profile: invalid Service Bus topic name for user {userId}: {topicValue}.");
                return null;
            }
        }

        // Validate required fields before caching
        if (string.IsNullOrEmpty(containerName) || string.IsNullOrEmpty(topicName))
        {
            _logger.LogWarning($"User profile: missing required async config fields (containername, topic) for user {userId}.");
            return null;
        }

        // Parse optional timeout (defaults to 3600)
        int timeoutSecs = 3600;
        if (asyncConfigParts.TryGetValue("timeout", out var timeoutValue))
        {
            if (!int.TryParse(timeoutValue, out timeoutSecs) || timeoutSecs <= 0)
            {
                timeoutSecs = 3600;
                _logger.LogWarning($"User profile: defaulting async blob access timeout for user {userId} to {timeoutSecs}s.");
            }
        }

        // Parse optional generateSAS (defaults to false)
        bool generateSasTokens = false;
        if (asyncConfigParts.TryGetValue("generatesas", out var sasValue))
        {
            if (!bool.TryParse(sasValue, out generateSasTokens))
            {
                generateSasTokens = false;
                _logger.LogWarning($"User profile: invalid generateSAS value for user {userId}, defaulting to false.");
            }
        }

        // Create and cache the result
        var newInfo = new AsyncClientInfo(userId, containerName, topicName, timeoutSecs, generateSasTokens);
        var updatedUserInformation = new Dictionary<string, AsyncClientInfo>(currentUserInformation)
        {
            [userId] = newInfo
        };
        Interlocked.Exchange(ref _userInformation, updatedUserInformation);
        return newInfo;
    }

    private static Dictionary<string, AsyncClientInfo> CreateDefaultAsyncClientInfoCache()
    {
        return new Dictionary<string, AsyncClientInfo>
        {
            [Constants.Server] = new AsyncClientInfo(Constants.Server, Constants.Server, Constants.Server, 3600)
        };
    }

    /// <summary>
    /// Removes soft-delete markers from an entry dictionary.
    /// </summary>
    private Dictionary<string, string> CleanSoftDeleteMarkers(Dictionary<string, string> entry)
    {
        var cleanEntry = new Dictionary<string, string>(entry);
        cleanEntry.Remove(s_DeletedAtKey);
        cleanEntry.Remove(s_ExpiresAtKey);
        return cleanEntry;
    }

    /// <summary>
    /// Validates Azure blob container name rules.
    /// </summary>
    private bool IsValidBlobContainerName(string name, out string validatedName)
    {
        validatedName = String.Empty;

        // Azure container names must be lowercase, 3-63 chars, and only letters, numbers, and dashes
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 3 || name.Length > 63) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$")) return false;
        if (name.StartsWith("-") || name.EndsWith("-")) return false;
        if (name.Contains("--")) return false;
        validatedName = name;
        return true;
    }

    /// <summary>
    /// Validates Azure Service Bus topic name rules.
    /// </summary>
    private bool IsValidServiceBusTopicName(string name, out string validatedName)
    {
        validatedName = String.Empty;

        // Azure Service Bus topic names: 1-260 chars, letters, numbers, periods, hyphens, underscores, forward slashes
        // Cannot start or end with period, hyphen, or forward slash
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 1 || name.Length > 260) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9._/-]+$")) return false;
        if (name.StartsWith(".") || name.StartsWith("-") || name.StartsWith("/")) return false;
        if (name.EndsWith(".") || name.EndsWith("-") || name.EndsWith("/")) return false;
        validatedName = name;
        return true;
    }



    /// <summary>
    /// Applies soft-delete logic to a dictionary of entries.
    /// Marks missing entries as deleted, preserves entries within grace period, and removes expired entries.
    /// </summary>
    /// <param name="currentData">The current snapshot of data</param>
    /// <param name="newData">The newly parsed data from config</param>
    /// <param name="entityType">The type of entity (e.g., "Profile", "AuthAppID") for logging</param>
    /// <returns>Tuple of (merged result, deleted count, list of deleted IDs, list of restored IDs)</returns>
    private (Dictionary<string, Dictionary<string, string>> result, int deletedCount, List<string> deletedIds, List<string> restoredIds)
        ApplySoftDeletes(
            Dictionary<string, Dictionary<string, string>> currentData,
            Dictionary<string, Dictionary<string, string>> newData,
            string entityType)
    {
        // Find entries that existed before but are missing in new config
        var missingEntries = currentData.Keys
            .Where(id => !newData.ContainsKey(id))
            .ToList();

        // Find restorations (entries that were deleted but now reappear in new config)
        var restoredEntries = newData.Keys
            .Where(id => currentData.ContainsKey(id) &&
                        currentData[id].ContainsKey(s_DeletedAtKey))
            .ToList();

        // Mark missing entries as deleted in-place
        int deletedCount = 0;
        var deletedIds = new List<string>();

        foreach (var id in missingEntries)
        {
            if (currentData.TryGetValue(id, out var existingEntry))
            {
                // Check if already marked as deleted and not expired
                bool alreadyDeleted = existingEntry.ContainsKey(s_DeletedAtKey) &&
                                     existingEntry.ContainsKey(s_ExpiresAtKey);

                if (alreadyDeleted && DateTime.TryParse(existingEntry[s_ExpiresAtKey], out var expiresAt))
                {
                    if (expiresAt > DateTime.UtcNow)
                    {
                        // Still within grace period - keep existing entry with timestamps
                        newData[id] = existingEntry;
                        continue;
                    }
                    // Expired - let it be removed (don't add to newData)
                }
                else
                {
                    // Not yet marked as deleted - mark it now
                    var now = DateTime.UtcNow;
                    var entryWithDeletion = new Dictionary<string, string>(existingEntry)
                    {
                        [s_DeletedAtKey] = now.ToString("o"),
                        [s_ExpiresAtKey] = now.Add(SoftDeleteExpirationPeriod).ToString("o")
                    };
                    newData[id] = entryWithDeletion;
                    deletedCount++;
                    deletedIds.Add(id);
                }
            }
        }

        // Log soft-delete events
        if (deletedCount > 0)
        {
            lock (_profileErrorEventLock)
            {
                _profileErrorEvent.ClearEventData();
                _profileErrorEvent.Type = EventType.ProfileError;
                _profileErrorEvent["Operation"] = "SoftDelete";
                _profileErrorEvent["EntityType"] = entityType;
                _profileErrorEvent["DeletedCount"] = deletedCount.ToString();
                _profileErrorEvent["DeletedIds"] = string.Join(",", deletedIds);
                _profileErrorEvent["GracePeriodMinutes"] = SoftDeleteExpirationPeriod.TotalMinutes.ToString("F0");
                _profileErrorEvent.SendEvent();
            }

            foreach (var id in deletedIds)
            {
                _logger.LogWarning($"{entityType} status - SOFT-DELETED - ID: {id}, grace period: {SoftDeleteExpirationPeriod.TotalMinutes:F0} min");
            }
        }

        // Log restoration events
        if (restoredEntries.Count > 0)
        {
            lock (_profileErrorEventLock)
            {
                _profileErrorEvent.ClearEventData();
                _profileErrorEvent.Type = EventType.ProfileError;
                _profileErrorEvent["Operation"] = "Restored";
                _profileErrorEvent["EntityType"] = entityType;
                _profileErrorEvent["RestoredCount"] = restoredEntries.Count.ToString();
                _profileErrorEvent["RestoredIds"] = string.Join(",", restoredEntries);
                _profileErrorEvent.SendEvent();
            }

            foreach (var id in restoredEntries)
            {
                _logger.LogWarning($"{entityType} status - RESTORED - ID: {id}");
            }
        }

        return (newData, deletedCount, deletedIds, restoredEntries);
    }

}