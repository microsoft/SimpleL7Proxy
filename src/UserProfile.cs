using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class DeletedProfile
{
    public string UserId { get; set; } = string.Empty;
    public DateTime DeletedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

// Tuned for read-heavy workloads .. profile updates only happen every hour
public class UserProfile : IUserProfile
{
    private readonly string lookupHeaderName;
    private IBackendOptions options;

    private volatile Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    private volatile List<string> suspendedUserProfiles = new List<string>();
    private volatile List<string> authAppIDs = new List<string>();
    private volatile Dictionary<string, DeletedProfile> deletedProfiles = new Dictionary<string, DeletedProfile>();
    private static bool _isInitialized = false;
    private static TimeSpan SoftDeleteExpirationPeriod ;

    public UserProfile(IBackendOptions options)
    {
        this.options = options;
        lookupHeaderName = options.LookupHeaderName;
        SoftDeleteExpirationPeriod = TimeSpan.FromMinutes(options.UserSoftDeleteTTLMinutes);
    }

    public enum ParsingMode
    {
        profileMode,
        SuspendedUserMode,
        AuthAppIDMode
    }

    public void StartBackgroundConfigReader(CancellationToken cancellationToken)
    {

        // create a new task that reads the user config every hour
        Task.Run(() => ConfigReader(cancellationToken), cancellationToken);
        
        // create a background task to cleanup expired soft-deleted profiles
        Task.Run(() => CleanupExpiredDeletedProfiles(cancellationToken), cancellationToken);

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
                await ReadUserConfigAsync(options.UserConfigUrl, ParsingMode.profileMode).ConfigureAwait(false);
                await ReadUserConfigAsync(options.SuspendedUserConfigUrl, ParsingMode.SuspendedUserMode).ConfigureAwait(false);
                await ReadUserConfigAsync(options.ValidateAuthAppIDUrl, ParsingMode.AuthAppIDMode).ConfigureAwait(false);

                // Count users, initialized when at least one user profile is loaded
                if (options.UserConfigRequired && userProfiles.Count > 0 && authAppIDs.Count > 0)
                {
                    _isInitialized = true;
                }
                else if (!options.UserConfigRequired)
                {
                    _isInitialized = true;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Error reading user config: {e.Message}");
                _isInitialized = false;
            }

            Console.WriteLine($"User profiles loaded - {userProfiles.Count} users found, {suspendedUserProfiles.Count} suspended users found, {authAppIDs.Count} auth app IDs found  Initialized: {_isInitialized} ");
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
        if (string.IsNullOrEmpty(config))
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
                    Console.Error.WriteLine($"Error reading user config file: {ex.Message}");
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
                    Console.Error.WriteLine($"Error reading user config from URL {location}: {e.Message}");
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
                lookupFieldName = lookupHeaderName;
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                lookupFieldName = options.ValidateAuthAppFieldName;
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
                            if (profile.TryGetProperty(options.ValidateAuthAppFieldName, out JsonElement authAppIdElement))
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
                                if (!property.Name.Equals(lookupHeaderName, StringComparison.OrdinalIgnoreCase))
                                {
                                    kvPairs[property.Name] = property.Value.ToString();
                                }
                            }
                            localUserProfiles[entityId] = kvPairs;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Profile field is missing {lookupHeaderName}. Skipping...");
                    }
                }
            }

            string entityName = "";
            int entityValue = 0;

            if (mode == ParsingMode.SuspendedUserMode)
            {
                Interlocked.Exchange(ref suspendedUserProfiles, localSuspendedUserProfiles);
                entityName = "Suspended Users";
                entityValue = suspendedUserProfiles.Count;
            }
            else if (mode == ParsingMode.AuthAppIDMode)
            {
                Interlocked.Exchange(ref authAppIDs, localAuthAppIDs);
                entityName = "AuthAppIDs";
                entityValue = authAppIDs.Count;
            }
            else if (mode == ParsingMode.profileMode)
            {
                // Capture current snapshot for comparison
                var currentProfiles = userProfiles;
                var currentDeleted = deletedProfiles;

                // Find profiles that existed before but are missing in new config
                var missingProfiles = currentProfiles.Keys
                    .Where(userId => !localUserProfiles.ContainsKey(userId))
                    .ToList();

                // Create new deleted profiles dictionary with updates
                var updatedDeleted = new Dictionary<string, DeletedProfile>(currentDeleted);

                // Soft-delete missing profiles that aren't already deleted
                int deletedCount = 0;
                foreach (var userId in missingProfiles)
                {
                    if (!updatedDeleted.ContainsKey(userId) || updatedDeleted[userId].ExpiresAt <= DateTime.UtcNow)
                    {
                        var now = DateTime.UtcNow;
                        updatedDeleted[userId] = new DeletedProfile
                        {
                            UserId = userId,
                            DeletedAt = now,
                            ExpiresAt = now.Add(SoftDeleteExpirationPeriod)
                        };
                        deletedCount++;
                    }
                }

                // Restore profiles that reappear in the new config
                var restoredProfiles = localUserProfiles.Keys
                    .Where(userId => updatedDeleted.ContainsKey(userId))
                    .ToList();

                foreach (var userId in restoredProfiles)
                {
                    updatedDeleted.Remove(userId);
                }

                // Atomically swap both dictionaries
                Interlocked.Exchange(ref userProfiles, localUserProfiles);
                Interlocked.Exchange(ref deletedProfiles, updatedDeleted);

                entityName = "User Profiles";
                entityValue = localUserProfiles.Count;

                if (deletedCount > 0)
                {
                    Console.WriteLine($"Soft-deleted {deletedCount} profiles that were removed from config");
                }
                if (restoredProfiles.Count > 0)
                {
                    Console.WriteLine($"Restored {restoredProfiles.Count} profiles that reappeared in config");
                }
            }

            Console.WriteLine($"Successfully parsed {entityName}.  Found {entityValue} user entities.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing user config: {ex.Message}");
        }
    }

    private async Task CleanupExpiredDeletedProfiles(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var currentDeleted = deletedProfiles;
                
                var expiredProfiles = currentDeleted
                    .Where(kvp => kvp.Value.ExpiresAt <= now)
                    .Select(kvp => kvp.Key)
                    .ToList();

                if (expiredProfiles.Count > 0)
                {
                    // Create new dictionary without expired profiles
                    var updatedDeleted = new Dictionary<string, DeletedProfile>(currentDeleted);
                    foreach (var userId in expiredProfiles)
                    {
                        updatedDeleted.Remove(userId);
                    }

                    // Atomically swap
                    Interlocked.Exchange(ref deletedProfiles, updatedDeleted);
                    Console.WriteLine($"Cleaned up {expiredProfiles.Count} expired soft-deleted profiles");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error cleaning up deleted profiles: {ex.Message}");
            }

            // Run cleanup every 30 minutes
            await Task.Delay(TimeSpan.FromMinutes(30), cancellationToken);
        }
    }

    public (Dictionary<string, string> profile, bool isSoftDeleted) GetUserProfile(string userId)
    {
        bool isSoftDeleted = IsUserDeleted(userId);
        
        if (userProfiles.TryGetValue(userId, out var profile))
        {
            return (profile, isSoftDeleted);
        }
        return (new Dictionary<string, string>(), isSoftDeleted);
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

    public bool IsUserDeleted(string userId)
    {
        if (!deletedProfiles.TryGetValue(userId, out var deletedProfile))
        {
            return false;
        }

        // Check if the deletion has expired
        if (deletedProfile.ExpiresAt <= DateTime.UtcNow)
        {
            // Expired, but don't remove here - cleanup task will handle it
            return false;
        }

        return true;
    }

}