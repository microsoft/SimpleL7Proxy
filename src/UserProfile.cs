using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
public class UserProfile : IUserProfile
{

    private IBackendOptions options;

    private Dictionary<string, Dictionary<string, string>> userProfiles = new Dictionary<string, Dictionary<string, string>>();
    public UserProfile(IBackendOptions options)
    {
        this.options = options;
    }

    public void StartBackgroundConfigReader(CancellationToken cancellationToken)
    {

        // create a new task that reads the user config every hour
        Task.Run(() => ConfigReader(cancellationToken), cancellationToken);

    }

    public async Task ConfigReader(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReadUserConfigAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // Log error
                Console.WriteLine($"Error reading user config: {e.Message}");
            }

            await Task.Delay(3600000, cancellationToken);
        }
    }

    public async Task ReadUserConfigAsync()
    {
        if (string.IsNullOrEmpty(options.UserConfigUrl))
        {
            Console.WriteLine("UserConfigUrl is not set.");
            return;
        }
        // Read user config from URL

        string fileContent = string.Empty;
        string location = options.UserConfigUrl;

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
                Console.WriteLine($"User config file: {location.Substring(5)} not found or not a JSON file");
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
            ParseUserConfig(fileContent);
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

    public void ParseUserConfig(string fileContent)
    {
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

            foreach (var profile in userConfig.EnumerateArray())
            {
                if (profile.TryGetProperty("userId", out JsonElement userIdElement))
                {
                    string userId = userIdElement.GetString();
                    if (!string.IsNullOrEmpty(userId))
                    {
                        Dictionary<string, string> kvPairs = new Dictionary<string, string>();
                        foreach (var property in profile.EnumerateObject())
                        {
                            if (!property.Name.Equals("userId", StringComparison.OrdinalIgnoreCase))
                            {
                                kvPairs[property.Name] = property.Value.ToString();
                            }
                        }
                        userProfiles[userId] = kvPairs;
                    }
                }
            }
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

}