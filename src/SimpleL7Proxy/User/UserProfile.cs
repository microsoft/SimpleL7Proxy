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
    private readonly string _lookupHeaderName;

    private readonly BackendOptions _options;
    private readonly ILogger<Server> _logger;

    private Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private List<string> suspendedUserProfiles = new List<string>();
    private List<string> authAppIDs = new List<string>();
    
    public UserProfile(IOptions<BackendOptions> options, ILogger<Server> logger)
    {
        _options = options.Value;
        _logger = logger;
        _lookupHeaderName = _options.LookupHeaderName;
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
                Console.WriteLine($"Error reading user config: {e.Message}");
            }

            await Task.Delay(3600000, cancellationToken);
        }
    }

    public async Task ReadUserConfigAsync(string config, ParsingMode mode)
    {
        if (string.IsNullOrEmpty(_options.UserConfigUrl))
        {
            Console.WriteLine($"{config} is not set.");
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
                    Console.WriteLine($"Error reading user config file: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"{config} file: {location.Substring(5)} not found or not a JSON file");
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
                    Console.WriteLine($"Error reading user config from URL {location}: {e.Message}");
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
            Console.WriteLine("No user config provided to parse.");
            return;
        }

        try
        {
            var userConfig = JsonSerializer.Deserialize<JsonElement>(fileContent ?? string.Empty);

            if (userConfig.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"User config is not an array. Skipping...");
                return;
            }

            string lookupFieldName = "";
            if (mode == ParsingMode.SuspendedUserMode || mode == ParsingMode.profileMode )
            {
                lookupFieldName = _lookupHeaderName;
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

                        if (mode == ParsingMode.profileMode) {

                            Dictionary<string, string> kvPairs = new Dictionary<string, string>();
                            foreach (var property in profile.EnumerateObject())
                            {
                                if (!property.Name.Equals(_lookupHeaderName, StringComparison.OrdinalIgnoreCase))
                                {
                                    kvPairs[property.Name] = property.Value.ToString();
                                }
                            }
                            localUserProfiles[entityId] = kvPairs;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Profile field is missing {_lookupHeaderName}. Skipping...");
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

            Console.WriteLine($"Successfully parsed {entityName}.  Found {entityValue} user entities.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing user config: {ex.Message}");
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

}