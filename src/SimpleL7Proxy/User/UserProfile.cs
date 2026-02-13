using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.User;

public class UserProfile : BackgroundService, IUserProfileService
{
    private readonly string lookupHeaderName;
    private readonly BackendOptions _options;

    private volatile Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private volatile List<string> suspendedUserProfiles = new List<string>();
    private volatile Dictionary<string, Dictionary<string, string>> authAppIDs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    private DateTime? lastSuccessfulProfileLoad = null;
    private volatile bool profilesAreStale = false;
    private TimeSpan? staleDuration = null;
    private volatile bool isInitialized = false;
    private TimeSpan SoftDeleteExpirationPeriod;
    private static readonly HttpClient httpClient = new HttpClient();
    
    // Reusable ProxyEvent for profile error logging to reduce allocations
    private readonly ProxyEvent _profileErrorEvent = new ProxyEvent(8);
    private readonly object _profileErrorEventLock = new object();
    
    // Special keys used to mark deleted profiles in-place
    private const string DeletedAtKey = "__DeletedAt";
    private const string ExpiresAtKey = "__ExpiresAt";

    private readonly ILogger<UserProfile> _logger;
    private readonly Dictionary<string, AsyncClientInfo> _userInformation = new();

    public UserProfile(BackendOptions options, ILogger<UserProfile> logger)
    {
        ArgumentNullException.ThrowIfNull(options, nameof(options));
        ArgumentNullException.ThrowIfNull(logger, nameof(logger));

        _options = options;
        _logger = logger;
        lookupHeaderName = options.UserIDFieldName;
        _userInformation[Constants.Server] = new AsyncClientInfo(Constants.Server, Constants.Server, Constants.Server, 3600);
        SoftDeleteExpirationPeriod = TimeSpan.FromMinutes(options.UserSoftDeleteTTLMinutes);
        _logger.LogDebug("[INIT] UserProfile service starting");
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        stoppingToken.Register(() =>
        {
            _logger.LogInformation("[SHUTDOWN] ⏹ User Profile Reader stopping");
        });

        // Initialize User Profiles
        if (_options.UseProfiles && !string.IsNullOrEmpty(_options.UserConfigUrl))
        {
            // create a new task that reads the user config every hour
            Task.Run(async () =>
            {
                while ( true) {
                    try
                    {
                        await ConfigReader(stoppingToken).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        lock (_profileErrorEventLock)
                        {
                            _profileErrorEvent.ClearEventData();
                            _profileErrorEvent.Type = EventType.ProfileError;
                            _profileErrorEvent["Message"] = "ConfigReader task failed";
                            _profileErrorEvent["Exception"] = ex.Message;
                            _profileErrorEvent.SendEvent();
                        }
                        Console.Error.WriteLine($"FATAL: ConfigReader task failed: {ex.Message}");
                    }
                }
            }, stoppingToken);
        }

        return Task.CompletedTask;
    }

    public async Task ConfigReader(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTime startTime = DateTime.UtcNow;
            int NormalDelayMs = _options.UserConfigRefreshIntervalSecs * 1000; // Configurable interval
            const int ErrorDelayMs = 3000; // 3 seconds

            var localIsInitialized = false;
            bool allConfigsLoadedSuccessfully = false;
            try
            {
                var (profilesLoadedSuccessfully, profilesError) = await ReadUserConfigAsync(_options.UserConfigUrl ?? string.Empty, ParsingMode.ProfileMode).ConfigureAwait(false);
                var (suspendedLoadedSuccessfully, suspendedError) = await ReadUserConfigAsync(_options.SuspendedUserConfigUrl ?? string.Empty, ParsingMode.SuspendedUserMode).ConfigureAwait(false);
                var (authAppIDsLoadedSuccessfully, authAppIDsError) = await ReadUserConfigAsync(_options.ValidateAuthAppIDUrl ?? string.Empty, ParsingMode.AuthAppIDMode).ConfigureAwait(false);

                // Log which configs succeeded/failed
                if (!profilesLoadedSuccessfully)
                {
                    lock (_profileErrorEventLock)
                    {
                        _profileErrorEvent.ClearEventData();
                        _profileErrorEvent.Type = EventType.ProfileError;
                        _profileErrorEvent["Message"] = "Failed to load user profile config";
                        _profileErrorEvent["ConfigUrl"] = _options.UserConfigUrl ?? "null";
                        _profileErrorEvent.SendEvent();
                    }
                    
                    // Calculate staleness for error message
                    var currentStaleDuration = lastSuccessfulProfileLoad.HasValue 
                        ? DateTime.UtcNow - lastSuccessfulProfileLoad.Value 
                        : TimeSpan.Zero;
                    if (userProfiles.Count > 0)
                    {
                        _logger.LogError($"ERROR loading configs: User profiles ({_options.UserConfigUrl}) [{profilesError}] - Stale {currentStaleDuration.TotalSeconds:F0}s");
                    }
                    else
                    {
                        _logger.LogError($"ERROR loading configs: User profiles ({_options.UserConfigUrl}) [{profilesError}] - No cached data available");
                    }
                }
                if (!suspendedLoadedSuccessfully && !string.IsNullOrEmpty(_options.SuspendedUserConfigUrl))
                    _logger.LogError($"ERROR loading configs: Suspended users ({_options.SuspendedUserConfigUrl}) [{suspendedError}]");
                if (!authAppIDsLoadedSuccessfully && !string.IsNullOrEmpty(_options.ValidateAuthAppIDUrl))
                    _logger.LogError($"ERROR loading configs: AuthAppIDs ({_options.ValidateAuthAppIDUrl}) [{authAppIDsError}]");

                // Track successful profile loads
                if (profilesLoadedSuccessfully)
                {
                    // Check if we're recovering from stale state
                    bool wasStale = profilesAreStale;
                    lastSuccessfulProfileLoad = DateTime.UtcNow;
                    
                    if (wasStale)
                    {
                        _logger.LogWarning($"RECOVERED loading configs: User profiles ({_options.UserConfigUrl})");
                    }
                }
                // Note: Stale data status is shown in Config status line below

                // Calculate staleness
                profilesAreStale = !profilesLoadedSuccessfully && userProfiles.Count > 0;
                staleDuration = profilesAreStale && lastSuccessfulProfileLoad.HasValue
                    ? DateTime.UtcNow - lastSuccessfulProfileLoad.Value
                    : null;
                
                // Determine if service is ready based on available data
                bool hasProfileData = userProfiles.Count > 0;
                bool hasAuthData = authAppIDs.Count > 0;
                allConfigsLoadedSuccessfully = profilesLoadedSuccessfully && suspendedLoadedSuccessfully && authAppIDsLoadedSuccessfully;
                
                // Check if stale profiles have exceeded TTL
                bool staleProfilesExpired = staleDuration.HasValue && staleDuration.Value >= SoftDeleteExpirationPeriod;
                
                if (_options.UserConfigRequired)
                {
                    // Fully initialized: all required configs loaded successfully with data
                    if (allConfigsLoadedSuccessfully && userProfiles.Count > 0 && hasAuthData)
                    {
                        localIsInitialized = true;
                    }
                    // Degraded mode: running on stale data (within TTL), trigger fast retry (3s)
                    else if (profilesAreStale && !staleProfilesExpired && hasAuthData)
                    {
                        _logger.LogCritical($"Service in degraded mode: {userProfiles.Count} stale profiles (stale for {staleDuration?.TotalSeconds:F0}s). Retrying every 3 seconds.");
                    }
                    // Not ready: missing required data or stale profiles expired
                    else
                    {
                        if (staleProfilesExpired)
                            _logger.LogCritical($"Service not ready: stale profiles exceeded TTL ({staleDuration?.TotalMinutes:F1} minutes).");
                        else if (!hasProfileData)
                            _logger.LogCritical("Service not ready: no profile data available.");
                        else if (!hasAuthData)
                            _logger.LogCritical("Service not ready: no auth app ID data available.");
                        else
                            _logger.LogCritical("Service not ready: missing required data.");
                    }
                }
                else
                {
                    localIsInitialized = true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error reading user config: {e.Message}");
            }

            // Determine overall health status
            string statusPrefix;
            if (profilesAreStale && staleDuration.HasValue)
            {
                statusPrefix = $"Config status - STALE {staleDuration.Value.TotalSeconds:F0}s";
            }
            else if (!allConfigsLoadedSuccessfully)
            {
                statusPrefix = "Config status - DEGRADED";
            }
            else
            {
                statusPrefix = "Config status - HEALTHY";
            }
            
            // Calculate active vs soft-deleted profile counts
            int activeProfiles = 0;
            int softDeletedProfiles = 0;
            var currentProfilesSnapshot = userProfiles;
            foreach (var profile in currentProfilesSnapshot.Values)
            {
                if (profile.ContainsKey(DeletedAtKey))
                    softDeletedProfiles++;
                else
                    activeProfiles++;
            }
            
            // Calculate active vs soft-deleted AuthAppID counts
            int activeAuthAppIDs = 0;
            int softDeletedAuthAppIDs = 0;
            var currentAuthAppIDsSnapshot = authAppIDs;
            foreach (var authApp in currentAuthAppIDsSnapshot.Values)
            {
                if (authApp.ContainsKey(DeletedAtKey))
                    softDeletedAuthAppIDs++;
                else
                    activeAuthAppIDs++;
            }
            
            _logger.LogInformation($"{statusPrefix} - Profiles: {currentProfilesSnapshot.Count} ({activeProfiles} active, {softDeletedProfiles} soft-deleted), Suspended: {suspendedUserProfiles.Count}, AuthAppIDs: {currentAuthAppIDsSnapshot.Count} ({activeAuthAppIDs} active, {softDeletedAuthAppIDs} soft-deleted) | Initialized: {isInitialized}");
            int baseDelay = localIsInitialized ? NormalDelayMs : ErrorDelayMs;
            int elapsedMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            int remainingDelay = Math.Max(0, baseDelay - elapsedMs);

            isInitialized = localIsInitialized;
            await Task.Delay(remainingDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool ServiceIsReady()
    {
        return isInitialized;
    }

    public async Task<(bool success, string? errorMessage)> ReadUserConfigAsync(string config, ParsingMode mode)
    {
        if (string.IsNullOrEmpty(config))
        {
            return (true, null); // Empty config is considered success (not required)
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
                    return (false, $"Error reading file: {ex.Message}");
                }
            }
            else
            {
                return (false, "File not found or not a JSON file");
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
                return (false, $"HTTP error: {e.Message}");
            }
        }

        if (!string.IsNullOrEmpty(fileContent))
        {
            ParseUserConfig(fileContent, mode);
            return (true, null);
        }
        
        return (false, "Empty file content");
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

    public void ParseUserConfig(string fileContent, ParsingMode mode)
    {

        Dictionary<string, Dictionary<string, string>> localUserProfiles = new Dictionary<string, Dictionary<string, string>>();
        List<string> localSuspendedUserProfiles = new List<string>();
        Dictionary<string, Dictionary<string, string>> localAuthAppIDs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(fileContent))
        {
            _logger.LogWarning("No user config provided to parse.");
            return;
        }

        try
        {
            var userConfig = JsonSerializer.Deserialize<JsonElement>(fileContent ?? string.Empty);

            if (userConfig.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning($"User config is not an array. Skipping...");
                return;
            }

            string lookupFieldName = "";
            if (mode == ParsingMode.SuspendedUserMode || mode == ParsingMode.ProfileMode )
            {
                lookupFieldName = lookupHeaderName;
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
                        if (mode == ParsingMode.SuspendedUserMode) {
                            localSuspendedUserProfiles.Add(entityId);
                            continue;
                        }

                        if (mode == ParsingMode.AuthAppIDMode) {
                            if (profile.TryGetProperty(_options.ValidateAuthAppFieldName, out JsonElement authAppIdElement))
                            {
                                string authAppId = authAppIdElement.GetString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(authAppId))
                                {
                                    // Create dictionary entry for auth app ID (can store additional properties if needed)
                                    Dictionary<string, string> authAppEntry = new Dictionary<string, string>();
                                    foreach (var property in profile.EnumerateObject())
                                    {
                                        if (!property.Name.Equals(_options.ValidateAuthAppFieldName, StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Skip reserved deletion marker keys if present in source
                                            if (property.Name == DeletedAtKey || property.Name == ExpiresAtKey)
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

                        if (mode == ParsingMode.ProfileMode) {

                            Dictionary<string, string> kvPairs = new Dictionary<string, string>();
                            
                            foreach (var property in profile.EnumerateObject())
                            {
                                if (!property.Name.Equals(lookupHeaderName, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Skip reserved deletion marker keys if present in source
                                    if (property.Name == DeletedAtKey || property.Name == ExpiresAtKey)
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
                        _logger.LogWarning($"Profile field is missing {lookupHeaderName}. Skipping...");
                    }
                }
            }

            if (mode == ParsingMode.SuspendedUserMode)
            {
                Interlocked.Exchange(ref suspendedUserProfiles, localSuspendedUserProfiles);
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                // Apply soft-delete logic using helper method
                var (result, _, _, _) = ApplySoftDeletes(authAppIDs, localAuthAppIDs, "AuthAppID");

                // Atomically swap dictionary
                Interlocked.Exchange(ref authAppIDs, result);
            }
            else if (mode == ParsingMode.ProfileMode)
            {
                // Apply soft-delete logic using helper method
                var (result, _, _, _) = ApplySoftDeletes(userProfiles, localUserProfiles, "Profile");

                // Atomically swap dictionary
                Interlocked.Exchange(ref userProfiles, result);
            }

            // Logging moved to Config status line for conciseness
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error parsing user config: {ex.Message}");
        }
    }

    public (Dictionary<string, string> profile, bool isSoftDeleted, bool isStale) GetUserProfile(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return (new Dictionary<string, string>(), false, false);
        }

        // Capture snapshots for thread safety
        var currentProfiles = userProfiles;
        var currentStale = profilesAreStale;

        // Check if profile exists
        if (currentProfiles.TryGetValue(userId, out var profile))
        {
            var (isValid, isSoftDeleted) = CheckSoftDeleteStatus(profile, userId, "Profile");
            
            if (!isValid)
            {
                // Expired - treat as not found
                return (new Dictionary<string, string>(), false, false);
            }
            
            if (isSoftDeleted || profile.ContainsKey(DeletedAtKey))
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
        // Fast path: Check ExpiresAtKey first with TryGetValue (single lookup instead of ContainsKey + indexer)
        // Most entries won't have deletion markers, so this is the common case
        if (!entry.TryGetValue(ExpiresAtKey, out var expiresAtStr))
        {
            // No expiration marker - active entry
            return (true, false);
        }
        
        // Has ExpiresAtKey - verify DeletedAtKey also exists for consistency
        if (!entry.ContainsKey(DeletedAtKey))
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
        // Check cache first (thread-safe)
        if (_userInformation.TryGetValue(userId, out var cachedInfo))
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
        _userInformation[userId] = newInfo;
        return newInfo;
    }
    /// <summary>
    /// Removes soft-delete markers from an entry dictionary.
    /// </summary>
    private Dictionary<string, string> CleanSoftDeleteMarkers(Dictionary<string, string> entry)
    {
        var cleanEntry = new Dictionary<string, string>(entry);
        cleanEntry.Remove(DeletedAtKey);
        cleanEntry.Remove(ExpiresAtKey);
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
                        currentData[id].ContainsKey(DeletedAtKey))
            .ToList();

        // Mark missing entries as deleted in-place
        int deletedCount = 0;
        var deletedIds = new List<string>();
        
        foreach (var id in missingEntries)
        {
            if (currentData.TryGetValue(id, out var existingEntry))
            {
                // Check if already marked as deleted and not expired
                bool alreadyDeleted = existingEntry.ContainsKey(DeletedAtKey) && 
                                     existingEntry.ContainsKey(ExpiresAtKey);
                
                if (alreadyDeleted && DateTime.TryParse(existingEntry[ExpiresAtKey], out var expiresAt))
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
                        [DeletedAtKey] = now.ToString("o"),
                        [ExpiresAtKey] = now.Add(SoftDeleteExpirationPeriod).ToString("o")
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