using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Backend;

public static class BackendHostConfigurationExtensions
{
  private static ILogger? _logger;
  public static IServiceCollection AddBackendHostConfiguration(this IServiceCollection services, ILogger logger)
  {
    var backendOptions = LoadBackendOptions();
    _logger = logger;

    services.Configure<BackendOptions>(options =>
    {
      options.AcceptableStatusCodes = backendOptions.AcceptableStatusCodes;
      options.Client = backendOptions.Client;
      options.CircuitBreakerErrorThreshold = backendOptions.CircuitBreakerErrorThreshold;
      options.CircuitBreakerTimeslice = backendOptions.CircuitBreakerTimeslice;
      options.DefaultPriority = backendOptions.DefaultPriority;
      options.DefaultTTLSecs = backendOptions.DefaultTTLSecs;
      options.HostName = backendOptions.HostName;
      options.Hosts = backendOptions.Hosts;
      options.IDStr = backendOptions.IDStr;
      options.LogHeaders = backendOptions.LogHeaders;
      options.LogProbes = backendOptions.LogProbes;
      options.MaxQueueLength = backendOptions.MaxQueueLength;
      options.OAuthAudience = backendOptions.OAuthAudience;
      options.PriorityKeys = backendOptions.PriorityKeys;
      options.PriorityValues = backendOptions.PriorityValues;
      options.Port = backendOptions.Port;
      options.PollInterval = backendOptions.PollInterval;
      options.PollTimeout = backendOptions.PollTimeout;
      options.SuccessRate = backendOptions.SuccessRate;
      options.Timeout = backendOptions.Timeout;
      options.TerminationGracePeriodSeconds = backendOptions.TerminationGracePeriodSeconds;
      options.UseOAuth = backendOptions.UseOAuth;
      options.UserProfileHeader = backendOptions.UserProfileHeader;
      options.UseProfiles = backendOptions.UseProfiles;
      options.UserConfigUrl = backendOptions.UserConfigUrl;
      options.UserPriorityThreshold = backendOptions.UserPriorityThreshold;
      options.PriorityWorkers = backendOptions.PriorityWorkers;
      options.Workers = backendOptions.Workers;
    });

    return services;
  }

  // Reads an environment variable and returns its value as an integer.
  // If the environment variable is not set, it returns the provided default value.
  private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (!int.TryParse(envValue, out var value))
    {
      _logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return value;
  }

  // Reads an environment variable and returns its value as an integer[].
  // If the environment variable is not set, it returns the provided default value.
  private static int[] ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(envValue))
    {
      _logger?.LogWarning($"Using default: {variableName}: {string.Join(",", defaultValues)}");
      return defaultValues;
    }
    try
    {
      return envValue.Split(',').Select(int.Parse).ToArray();
    }
    catch (Exception)
    {
      _logger?.LogWarning($"Could not parse {variableName} as an integer array, using default: {string.Join(",", defaultValues)}");
      return defaultValues;
    }
  }

  // Reads an environment variable and returns its value as a float.
  // If the environment variable is not set, it returns the provided default value.
  private static float ReadEnvironmentVariableOrDefault(string variableName, float defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (!float.TryParse(envValue, out var value))
    {
      Console.WriteLine($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return value;
  }
  // Reads an environment variable and returns its value as a string.
  // If the environment variable is not set, it returns the provided default value.
  private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(envValue))
    {
      _logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return envValue.Trim();
  }

  // Reads an environment variable and returns its value as a string.
  // If the environment variable is not set, it returns the provided default value.
  private static bool ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(envValue))
    {
      _logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return envValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
  }

  // Converts a List<string> to a dictionary of integers.
  private static Dictionary<int, int> KVPairs(List<string> list)
  {
    Dictionary<int, int> keyValuePairs = new Dictionary<int, int>();
    foreach (var item in list)
    {
      var kvp = item.Split(':');
      if (int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value))
      {
        keyValuePairs.Add(key, value);
      }
      else
      {
        _logger?.LogWarning($"Could not parse {item} as a key-value pair, ignoring");
      }
    }

    return keyValuePairs;
  }

  // Converts a comma-separated string to a list of strings.
  private static List<string> ToListOfString(string s)
  {
    return s.Split(',').Select(p => p.Trim()).ToList();
  }

  // Converts a comma-separated string to a list of integers.
  private static List<int> ToListOfInt(string s)
  {

    // parse each value in the list
    List<int> ints = new List<int>();
    foreach (var item in s.Split(','))
    {
      if (int.TryParse(item.Trim(), out int value))
      {
        ints.Add(value);
      }
      else
      {
        _logger?.LogWarning($"Could not parse {item} as an integer, defaulting to 5");
        ints.Add(5);
      }
    }

    return s.Split(',').Select(p => int.Parse(p.Trim())).ToList();
  }



  // Loads backend options from environment variables or uses default values if the variables are not set.
  // It also configures the DNS refresh timeout and sets up an HttpClient instance.
  // If the IgnoreSSLCert environment variable is set to true, it configures the HttpClient to ignore SSL certificate errors.
  // If the AppendHostsFile environment variable is set to true, it appends the IP addresses and hostnames to the /etc/hosts file.
  private static BackendOptions LoadBackendOptions()
  {
    // Read and set the DNS refresh timeout from environment variables or use the default value
    var DNSTimeout = ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 120000);
    ServicePointManager.DnsRefreshTimeout = DNSTimeout;

    // Initialize HttpClient and configure it to ignore SSL certificate errors if specified in environment variables.
    HttpClient _client = new();
    if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
    {
      _client = new(new HttpClientHandler
      {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
      });
    }

    string replicaID = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REPLICA_NAME", "01");

#if DEBUG
    // Load appsettings.json only in Debug mode
    var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

    foreach (var setting in configuration.GetSection("Settings").GetChildren())
    {
      Environment.SetEnvironmentVariable(setting.Key, setting.Value);
    }
#endif

    // Create and return a BackendOptions object populated with values from environment variables or default values.
    var backendOptions = new BackendOptions
    {
      AcceptableStatusCodes = ReadEnvironmentVariableOrDefault("AcceptableStatusCodes", new int[] { 200, 401, 403, 404, 408, 410, 412, 417, 400 }),
      Client = _client,
      CircuitBreakerErrorThreshold = ReadEnvironmentVariableOrDefault("CBErrorThreshold", 50),
      CircuitBreakerTimeslice = ReadEnvironmentVariableOrDefault("CBTimeslice", 60),
      DefaultPriority = ReadEnvironmentVariableOrDefault("DefaultPriority", 2),
      DefaultTTLSecs = ReadEnvironmentVariableOrDefault("DefaultTTLSecs", 300),
      HostName = ReadEnvironmentVariableOrDefault("Hostname", "Default"),
      Hosts = new List<BackendHostConfig>(),
      IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-",
      LogHeaders = ReadEnvironmentVariableOrDefault("LogHeaders", "").Split(',').Select(x => x.Trim()).ToList(),
      LogProbes = ReadEnvironmentVariableOrDefault("LogProbes", false),
      MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
      OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
      Port = ReadEnvironmentVariableOrDefault("Port", 80),
      PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
      PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
      PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
      PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
      SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
      Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
      TerminationGracePeriodSeconds = ReadEnvironmentVariableOrDefault("TERMINATION_GRACE_PERIOD_SECONDS", 30),
      UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", false),
      UserProfileHeader = ReadEnvironmentVariableOrDefault("UserProfileHeader", "X-UserProfile"),
      UseProfiles = ReadEnvironmentVariableOrDefault("UseProfiles", false),
      UserConfigUrl = ReadEnvironmentVariableOrDefault("UserConfigUrl", "file:config.json"),
      UserPriorityThreshold = ReadEnvironmentVariableOrDefault("UserPriorityThreshold", 0.1f),
      PriorityWorkers = KVPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", "2:1,3:1"))),
      Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
    };

    backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);

    int i = 1;
    StringBuilder sb = new();
    while (true)
    {

      var hostname = Environment.GetEnvironmentVariable($"Host{i}")?.Trim();
      if (string.IsNullOrEmpty(hostname)) break;

      var probePath = Environment.GetEnvironmentVariable($"Probe_path{i}")?.Trim();
      var ip = Environment.GetEnvironmentVariable($"IP{i}")?.Trim();

      try
      {
        _logger?.LogInformation($"Found host {hostname} with probe path {probePath} and IP {ip}");

        BackendHostConfig bh = new BackendHostConfig(hostname, probePath);

        backendOptions.Hosts.Add(bh);

        sb.AppendLine($"{ip} {bh.Host}");
      }
      catch (UriFormatException e)
      {
        _logger?.LogError($"Could not add Host{i} with {hostname} : {e.Message}");
      }

      i++;
    }

    if (Environment.GetEnvironmentVariable("APPENDHOSTSFILE")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
        Environment.GetEnvironmentVariable("AppendHostsFile")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
    {
      _logger?.LogInformation($"Appending {sb} to /etc/hosts");
      using StreamWriter sw = File.AppendText("/etc/hosts");
      sw.WriteLine(sb.ToString());
    }

    // confirm the number of priority keys and values match
    if (backendOptions.PriorityKeys.Count != backendOptions.PriorityValues.Count)
    {
      Console.WriteLine("The number of PriorityKeys and PriorityValues do not match in length, defaulting all values to 5");
      backendOptions.PriorityValues = Enumerable.Repeat(5, backendOptions.PriorityKeys.Count).ToList();
    }

    // confirm that the PriorityWorkers Key's have a corresponding priority keys
    int workerAllocation = 0;
    foreach (var key in backendOptions.PriorityWorkers.Keys)
    {
      if (!(backendOptions.PriorityValues.Contains(key) || key == backendOptions.DefaultPriority))
      {
        Console.WriteLine($"WARNING: PriorityWorkers Key {key} does not have a corresponding PriorityKey");
      }
      workerAllocation += backendOptions.PriorityWorkers[key];
    }

    if (workerAllocation > backendOptions.Workers)
    {
      Console.WriteLine($"WARNING: Worker allocation exceeds total number of workers:{workerAllocation} > {backendOptions.Workers}");
      Console.WriteLine($"Adjusting total number of workers to {workerAllocation}. Fix PriorityWorkers if it isn't what you want.");
      backendOptions.Workers = workerAllocation;
    }

    return backendOptions;
  }


}
