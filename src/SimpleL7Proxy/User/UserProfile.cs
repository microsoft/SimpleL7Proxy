using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.User;

public class UserProfile : BackgroundService, IUserProfileService
{
    private readonly string _UserIDFieldName;

    private readonly BackendOptions _options;
    private readonly ILogger<UserProfile> _logger;
    private Dictionary<string, AsyncClientInfo> _userInformation = new();

    private Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private List<string> suspendedUserProfiles = new List<string>();
    private List<string> authAppIDs = new List<string>();

    private static bool _isInitialized = false;

    public UserProfile(IOptions<BackendOptions> options, ILogger<UserProfile> logger)
    {
        _options = options.Value;
        _logger = logger;
        _UserIDFieldName = _options.UserIDFieldName;
        _userInformation[Constants.Server] = new AsyncClientInfo(Constants.Server, Constants.Server, Constants.Server, false, 3600);

        _logger.LogDebug("[INIT] UserProfile service starting");
    }
    public enum ParsingMode
    {
        profileMode,
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
            const int NormalDelayMs = 3600000; // 1 hour
            const int ErrorDelayMs = 3000; // 3 seconds

            try
            {
                await ReadUserConfigAsync(_options.UserConfigUrl, ParsingMode.profileMode).ConfigureAwait(false);
                await ReadUserConfigAsync(_options.SuspendedUserConfigUrl, ParsingMode.SuspendedUserMode).ConfigureAwait(false);
                await ReadUserConfigAsync(_options.ValidateAuthAppIDUrl, ParsingMode.AuthAppIDMode).ConfigureAwait(false);

                // Count users, initialized when at least one user profile is loaded
                if (_options.UserConfigRequired && userProfiles.Count > 0 && authAppIDs.Count > 0)
                {
                    _isInitialized = true;
                }
                else if (!_options.UserConfigRequired)
                {
                    _isInitialized = true;
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error reading user config: {e.Message}");
                _isInitialized = false;
            }

            _logger.LogInformation($"[DATA] ✓ User profiles loaded - {userProfiles.Count} users found, {suspendedUserProfiles.Count} suspended users found, {authAppIDs.Count} auth app IDs found  Initialized: {_isInitialized} " );

            int baseDelay = _isInitialized ? NormalDelayMs : ErrorDelayMs;
            int elapsedMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            int remainingDelay = Math.Max(0, baseDelay - elapsedMs);
            await Task.Delay(remainingDelay, cancellationToken);
        }
    }

    public bool ServiceIsReady()
    {
        return _isInitialized;
    }

    public async Task ReadUserConfigAsync(string config, ParsingMode mode)
    {
        if (string.IsNullOrEmpty(_options.UserConfigUrl))
        {
            _logger.LogInformation($"{config} is not set.");
            return;
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
                    fileContent = await File.ReadAllTextAsync(file.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error reading user config file: {ex.Message}");
                }
            }
            else
            {
                _logger.LogInformation($"{config} file: {location.Substring(5)} not found or not a JSON file");
            }
        }
        else
        {
            // Read from URL
            using (var client = new HttpClient())
            {
                try
                {
                    fileContent = await client.GetStringAsync(location);
                }
                catch (HttpRequestException e)
                {
                    _logger.LogError($"Error reading user config from URL {location}: {e.Message}");
                }
            }
        }

        if (!string.IsNullOrEmpty(fileContent))
        {
            ParseUserConfig(fileContent, mode);
        }
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
        List<string> localAuthAppIDs = new List<string>();

        if (string.IsNullOrWhiteSpace(fileContent))
        {
            _logger.LogInformation("No user config provided to parse.");
            return;
        }

        try
        {
            var userConfig = JsonSerializer.Deserialize<JsonElement>(fileContent ?? string.Empty);

            if (userConfig.ValueKind != JsonValueKind.Array)
            {
                _logger.LogInformation($"User config is not an array. Skipping...");
                return;
            }

            string lookupFieldName = "";
            if (mode == ParsingMode.SuspendedUserMode || mode == ParsingMode.profileMode)
            {
                lookupFieldName = _UserIDFieldName;
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
                                    localAuthAppIDs.Add(authAppId);
                                }
                            }
                            continue;
                        }

                        if (mode == ParsingMode.profileMode)
                        {

                            Dictionary<string, string> kvPairs = new Dictionary<string, string>();
                            foreach (var property in profile.EnumerateObject())
                            {
                                if (!property.Name.Equals(_UserIDFieldName, StringComparison.OrdinalIgnoreCase))
                                {
                                    kvPairs[property.Name] = property.Value.ToString();
                                }
                            }
                            localUserProfiles[entityId] = kvPairs;
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"Profile field is missing {_UserIDFieldName}. Skipping...");
                    }
                }
            }

            string entityName = "";
            int entityValue = 0;

            if (mode == ParsingMode.SuspendedUserMode)
            {
                suspendedUserProfiles = localSuspendedUserProfiles;
                entityName = "Suspended Users";
                entityValue = suspendedUserProfiles.Count;
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                authAppIDs = localAuthAppIDs;
                entityName = "AuthAppIDs";
                entityValue = authAppIDs.Count;
            }
            else if (mode == ParsingMode.profileMode)
            {
                userProfiles = localUserProfiles;
                entityName = "User Profiles";
                entityValue = userProfiles.Count;
            }

            _logger.LogInformation($"[DATA] ✓ {entityName} loaded - {entityValue} entities found");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error parsing user config: {ex.Message}");
        }
    }

    public Dictionary<string, string> GetUserProfile(string userId)
    {
        if (userProfiles.ContainsKey(userId))
        {
            return userProfiles[userId];
        }
        return new Dictionary<string, string>();
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
                    asyncEnabled = false;
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

}