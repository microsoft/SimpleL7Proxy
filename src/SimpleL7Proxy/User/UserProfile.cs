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
    private volatile HashSet<string> authAppIDs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private DateTime? lastSuccessfulProfileLoad = null;
    private volatile bool profilesAreStale = false;
    private TimeSpan? staleDuration = null;
    private volatile bool isInitialized = false;
    private TimeSpan SoftDeleteExpirationPeriod;
    private static readonly HttpClient httpClient = new HttpClient();
    
    // Reusable ProxyEvent for profile error logging to reduce allocations
    private readonly ProxyEvent _profileErrorEvent = new ProxyEvent(8);
    
    // Special keys used to mark deleted profiles in-place
    private const string DeletedAtKey = "__DeletedAt";
    private const string ExpiresAtKey = "__ExpiresAt";

    private readonly ILogger<UserProfile> _logger;
    private Dictionary<string, AsyncClientInfo> _userInformation = new();

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
            return Task.Run(() => ConfigReader(stoppingToken), stoppingToken);
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
                    _profileErrorEvent.ClearEventData();
                    _profileErrorEvent.Type = EventType.ProfileError;
                    _profileErrorEvent["Message"] = "Failed to load user profile config";
                    _profileErrorEvent["ConfigUrl"] = _options.UserConfigUrl ?? "null";
                    _profileErrorEvent.SendEvent();
                    
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
            
            _logger.LogInformation($"{statusPrefix} - Profiles: {currentProfilesSnapshot.Count} ({activeProfiles} active, {softDeletedProfiles} soft-deleted), Suspended: {suspendedUserProfiles.Count}, AuthAppIDs: {authAppIDs.Count} | Initialized: {isInitialized}");
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
        HashSet<string> localAuthAppIDs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                                    localAuthAppIDs.Add(authAppId);
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
                Interlocked.Exchange(ref authAppIDs, localAuthAppIDs);
            }
            else if (mode == ParsingMode.ProfileMode)
            {
                // Capture current snapshot for comparison
                var currentProfiles = userProfiles;

                // Find profiles that existed before but are missing in new config
                var missingProfiles = currentProfiles.Keys
                    .Where(userId => !localUserProfiles.ContainsKey(userId))
                    .ToList();

                // Count restorations BEFORE processing (profiles that were deleted but now reappear in new config)
                var restoredProfiles = localUserProfiles.Keys
                    .Where(userId => currentProfiles.ContainsKey(userId) && 
                                   currentProfiles[userId].ContainsKey(DeletedAtKey))
                    .ToList();

                // Mark missing profiles as deleted in-place (keep them in dictionary with deletion timestamps)
                int deletedCount = 0;
                foreach (var userId in missingProfiles)
                {
                    if (currentProfiles.TryGetValue(userId, out var existingProfile))
                    {
                        // Check if already marked as deleted and not expired
                        bool alreadyDeleted = existingProfile.ContainsKey(DeletedAtKey) && 
                                             existingProfile.ContainsKey(ExpiresAtKey);
                        
                        if (alreadyDeleted && DateTime.TryParse(existingProfile[ExpiresAtKey], out var expiresAt))
                        {
                            // Already deleted - check if expired
                            if (expiresAt > DateTime.UtcNow)
                            {
                                // Still within grace period - keep existing profile with timestamps
                                localUserProfiles[userId] = existingProfile;
                                continue;
                            }
                            // Expired - let it be removed (don't add to localUserProfiles)
                        }
                        else
                        {
                            // Not yet marked as deleted - mark it now
                            var now = DateTime.UtcNow;
                            var profileWithDeletion = new Dictionary<string, string>(existingProfile)
                            {
                                [DeletedAtKey] = now.ToString("o"),
                                [ExpiresAtKey] = now.Add(SoftDeleteExpirationPeriod).ToString("o")
                            };
                            localUserProfiles[userId] = profileWithDeletion;
                            deletedCount++;
                        }
                    }
                }

                // Atomically swap dictionary
                Interlocked.Exchange(ref userProfiles, localUserProfiles);

                // Only log if there were changes
                if (deletedCount > 0)
                {
                    var deletedUserIds = missingProfiles.Where(userId => 
                        localUserProfiles.ContainsKey(userId) && 
                        localUserProfiles[userId].ContainsKey(DeletedAtKey)).ToList();
                    
                    _profileErrorEvent.ClearEventData();
                    _profileErrorEvent.Type = EventType.ProfileError;
                    _profileErrorEvent["Operation"] = "SoftDelete";
                    _profileErrorEvent["DeletedCount"] = deletedCount.ToString();
                    _profileErrorEvent["DeletedUserIds"] = string.Join(",", deletedUserIds);
                    _profileErrorEvent["GracePeriodMinutes"] = SoftDeleteExpirationPeriod.TotalMinutes.ToString("F0");
                    _profileErrorEvent.SendEvent();
                    
                    foreach (var userId in deletedUserIds)
                    {
                        _logger.LogWarning($"Profile status - SOFT-DELETED - userID: {userId}, grace period: {SoftDeleteExpirationPeriod.TotalMinutes:F0} min");
                    }
                }
                if (restoredProfiles.Count > 0)
                {
                    _profileErrorEvent.ClearEventData();
                    _profileErrorEvent.Type = EventType.ProfileError;
                    _profileErrorEvent["Operation"] = "Restored";
                    _profileErrorEvent["RestoredCount"] = restoredProfiles.Count.ToString();
                    _profileErrorEvent["RestoredUserIds"] = string.Join(",", restoredProfiles);
                    _profileErrorEvent.SendEvent();
                    
                    foreach (var userId in restoredProfiles)
                    {
                        _logger.LogWarning($"Profile status - RESTORED - userID: {userId}");
                    }
                }
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
            // Check for deletion markers
            bool isMarkedDeleted = profile.ContainsKey(DeletedAtKey) && profile.ContainsKey(ExpiresAtKey);
            
            if (isMarkedDeleted)
            {
                // Check if deletion has expired
                if (DateTime.TryParse(profile[ExpiresAtKey], out var expiresAt))
                {
                    if (expiresAt > DateTime.UtcNow)
                    {
                        // Within grace period - return profile without deletion markers
                        var cleanProfile = new Dictionary<string, string>(profile);
                        cleanProfile.Remove(DeletedAtKey);
                        cleanProfile.Remove(ExpiresAtKey);
                        return (cleanProfile, true, currentStale);
                    }
                    // Expired - treat as not found
                    return (new Dictionary<string, string>(), false, false);
                }
                else
                {
                    // Failed to parse expiration timestamp - log error but allow profile through
                    // DEFENSIVE: Prefer false positive (allow corrupted soft-delete) over false negative (block legitimate user)
                    _profileErrorEvent.ClearEventData();
                    _profileErrorEvent.Type = EventType.ProfileError;
                    _profileErrorEvent["Message"] = "Failed to parse deletion timestamp - treating as active";
                    _profileErrorEvent["UserId"] = userId;
                    _profileErrorEvent["Timestamp"] = profile[ExpiresAtKey];
                    _profileErrorEvent.SendEvent();
                    
                    // Return profile without corruption markers to avoid propagating bad data
                    var cleanProfile = new Dictionary<string, string>(profile);
                    cleanProfile.Remove(DeletedAtKey);
                    cleanProfile.Remove(ExpiresAtKey);
                    return (cleanProfile, false, currentStale);
                }
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

        // Check if the authAppId is in the list of valid authAppIDs
        return authAppIDs.Contains(authAppId);
    }

    public AsyncClientInfo? GetAsyncParams(string userId)
    {
        if (_userInformation.TryGetValue(userId, out var cachedInfo))
        {
            return cachedInfo;
        }

        if (!userProfiles.TryGetValue(userId, out var data))
        {
            _logger.LogWarning($"User profile: profile for user {userId} not found.");
            return null;
        }

        // Check if async processing is enabled
        // async-config=enabled=true, containername=user123456, topic=status-123456, timeout=3600

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


        bool asyncEnabled = false;
        string containerName = string.Empty;
        string topicName = string.Empty;
        int timeoutSecs = 0;
        bool generateSasTokens = false; // Default to false - SAS tokens not generated unless explicitly requested

        foreach (var keyValuePair in asyncConfigParts)
        {
            var field = keyValuePair.Key;
            var value = keyValuePair.Value;

            if (field == "enabled")
            {
                if (!bool.TryParse(value, out asyncEnabled) || !asyncEnabled)
                {
                    _logger.LogWarning($"User profile: async mode not allowed for user {userId}.");
                    return null;
                }
            }
            else if (field == "containername")
            {
                if (!IsValidBlobContainerName(value, out containerName))
                {
                    _logger.LogWarning($"User profile: invalid blob container name for user {userId}: {value}.");
                    return null;
                }
            }
            else if (field == "topic")
            {
                if (!IsValidServiceBusTopicName(value, out topicName))
                {
                    _logger.LogWarning($"User profile: invalid Service Bus topic name for user {userId}: {value}.");
                    return null;
                }
            }
            else if (field == "timeout")
            {
                if (!int.TryParse(value, out timeoutSecs) || timeoutSecs <= 0)
                {
                    timeoutSecs = 3600;
                    _logger.LogWarning($"User profile: defaulting async blob access timeout for user {userId} with {timeoutSecs}.");
                }
            }
            else if (field == "generatesas")
            {
                if (!bool.TryParse(value, out generateSasTokens))
                {
                    generateSasTokens = false;
                    _logger.LogWarning($"User profile: invalid generateSAS value for user {userId}, defaulting to false.");
                }
            }
        }

        cachedInfo = new AsyncClientInfo(userId, containerName, topicName, timeoutSecs, generateSasTokens);
        _userInformation[userId] = cachedInfo;
        return cachedInfo;
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

    public bool IsUserDeleted(string userId)
    {
        var currentProfiles = userProfiles;
        
        if (!currentProfiles.TryGetValue(userId, out var profile))
        {
            return false;
        }

        // Check for deletion markers
        if (profile.ContainsKey(DeletedAtKey) && profile.ContainsKey(ExpiresAtKey))
        {
            // Check if deletion has expired
            if (DateTime.TryParse(profile[ExpiresAtKey], out var expiresAt))
            {
                return expiresAt > DateTime.UtcNow;
            }
            else
            {
                // Failed to parse expiration timestamp - log error and treat as not deleted
                _profileErrorEvent.ClearEventData();
                _profileErrorEvent.Type = EventType.ProfileError;
                _profileErrorEvent["Message"] = "Failed to parse deletion timestamp in IsUserDeleted";
                _profileErrorEvent["UserId"] = userId;
                _profileErrorEvent["Timestamp"] = profile[ExpiresAtKey];
                _profileErrorEvent.SendEvent();
            }
        }

        return false;
    }

}