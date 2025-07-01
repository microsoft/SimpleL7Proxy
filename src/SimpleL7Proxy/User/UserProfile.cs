using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.User;

public class UserProfile : BackgroundService, IUserProfileService
{
    private readonly string _UserIDFieldName;

    private readonly BackendOptions _options;
    private readonly ILogger<Server> _logger;
    private Dictionary<string, AsyncClientInfo> _userInformation = new();

    private Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private List<string> suspendedUserProfiles = new List<string>();
    private List<string> authAppIDs = new List<string>();

    public UserProfile(IOptions<BackendOptions> options, ILogger<Server> logger)
    {
        _options = options.Value;
        _logger = logger;
        _UserIDFieldName = _options.UserIDFieldName;
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
            _logger.LogInformation("User Profile Reader stopping.");
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
            try
            {
                await ReadUserConfigAsync(_options.UserConfigUrl, ParsingMode.profileMode).ConfigureAwait(false);
                await ReadUserConfigAsync(_options.SuspendedUserConfigUrl, ParsingMode.SuspendedUserMode).ConfigureAwait(false);
                await ReadUserConfigAsync(_options.ValidateAuthAppIDUrl, ParsingMode.AuthAppIDMode).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Log error
                _logger.LogInformation($"Error reading user config: {e.Message}");
            }

            await Task.Delay(3600000, cancellationToken);
        }
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

            _logger.LogInformation($"Successfully parsed {entityName}.  Found {entityValue} user entities.");
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

    // public bool AsyncAllowed(string UserId)
    // {
    //     if (userProfiles.ContainsKey(UserId))
    //     {
    //         var data = userProfiles[UserId];
    //         if (data.TryGetValue(_options.AsyncClientAllowedFieldName, out string? asyncAllowed))
    //         {
    //             return asyncAllowed.Equals("true", StringComparison.OrdinalIgnoreCase);
    //         }
    //     }

    //     return false;
    // }

    public AsyncClientInfo? GetAsyncParams(string userId)
    {
        if (_userInformation.TryGetValue(userId, out var cachedInfo))
        {
            return cachedInfo;
        }

        if (!userProfiles.TryGetValue(userId, out var data))
        {
            _logger.LogWarning($"User profile for {userId} not found.");
            return null;
        }

        if (!data.TryGetValue(_options.AsyncClientAllowedFieldName, out var asyncAllowed) ||
            !string.Equals(asyncAllowed, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning($"Async mode not allowed for user {userId}.");
            return null;
        }

        if (!data.TryGetValue(_options.AsyncClientBlobFieldname, out var containerName) ||
            string.IsNullOrWhiteSpace(containerName) ||
            !IsValidBlobContainerName(containerName))
        {
            _logger.LogWarning($"Invalid or missing blob container name for user {userId}.");
            return null;
        }

        if (!data.TryGetValue(_options.AsyncSBTopicFieldName, out var topicName) ||
            string.IsNullOrWhiteSpace(topicName) ||
            !IsValidBlobContainerName(topicName))
        {
            _logger.LogWarning($"Invalid or missing Service Bus topic name for user {userId}.");
            return null;
        }

        if (!data.TryGetValue(_options.AsyncClientBlobTimeoutFieldName, out var timeoutStr) ||
            !int.TryParse(timeoutStr, out var timeoutSecs) || timeoutSecs <= 0)
        {
            _logger.LogWarning($"Invalid or missing async blob access timeout for user {userId}. Using default value of 3600.");
            timeoutSecs = 3600; // Default to 1 hour if not specified
        }

        cachedInfo = new AsyncClientInfo(userId, containerName, topicName, timeoutSecs);
        _userInformation[userId] = cachedInfo;
        return cachedInfo;
    }

    /// <summary>
    /// Validates Azure blob container name rules.
    /// </summary>
    private bool IsValidBlobContainerName(string name)
    {
        // Azure container names must be lowercase, 3-63 chars, and only letters, numbers, and dashes
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length < 3 || name.Length > 63) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$")) return false;
        if (name.StartsWith("-") || name.EndsWith("-")) return false;
        if (name.Contains("--")) return false;
        return true;
    }

}