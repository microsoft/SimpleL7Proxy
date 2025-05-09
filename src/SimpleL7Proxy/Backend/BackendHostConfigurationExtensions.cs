﻿using Microsoft.Extensions.Configuration;
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
      options.DisallowedHeaders = backendOptions.DisallowedHeaders;
      options.HostName = backendOptions.HostName;
      options.Hosts = backendOptions.Hosts;
      options.IDStr = backendOptions.IDStr;
      options.LogAllRequestHeaders = backendOptions.LogAllRequestHeaders;
      options.LogAllRequestHeadersExcept = backendOptions.LogAllRequestHeadersExcept;
      options.LogAllResponseHeaders = backendOptions.LogAllResponseHeaders;
      options.LogAllResponseHeadersExcept = backendOptions.LogAllResponseHeadersExcept;
      options.LogHeaders = backendOptions.LogHeaders;
      options.LogProbes = backendOptions.LogProbes;
      options.LookupHeaderName = backendOptions.LookupHeaderName;
      options.MaxQueueLength = backendOptions.MaxQueueLength;
      options.OAuthAudience = backendOptions.OAuthAudience;
      options.PriorityKeyHeader = backendOptions.PriorityKeyHeader;
      options.PriorityKeys = backendOptions.PriorityKeys;
      options.PriorityValues = backendOptions.PriorityValues;
      options.Port = backendOptions.Port;
      options.PollInterval = backendOptions.PollInterval;
      options.PollTimeout = backendOptions.PollTimeout;
      options.RequiredHeaders = backendOptions.RequiredHeaders;
      options.SuccessRate = backendOptions.SuccessRate;
      options.SuspendedUserConfigUrl = backendOptions.SuspendedUserConfigUrl;
      options.Timeout = backendOptions.Timeout;
      options.TimeoutHeader = backendOptions.TimeoutHeader;
      options.TerminationGracePeriodSeconds = backendOptions.TerminationGracePeriodSeconds;
      options.TTLHeader = backendOptions.TTLHeader;
      options.UniqueUserHeaders = backendOptions.UniqueUserHeaders;
      options.UseOAuth = backendOptions.UseOAuth;
      options.UseOAuthGov = backendOptions.UseOAuthGov;
      options.UserProfileHeader = backendOptions.UserProfileHeader;
      options.UseProfiles = backendOptions.UseProfiles;
      options.UserConfigUrl = backendOptions.UserConfigUrl;
      options.UserPriorityThreshold = backendOptions.UserPriorityThreshold;
      options.ValidateHeaders = backendOptions.ValidateHeaders;
      options.PriorityWorkers = backendOptions.PriorityWorkers;
      options.ValidateAuthAppID = backendOptions.ValidateAuthAppID;
      options.ValidateAuthAppFieldName = backendOptions.ValidateAuthAppFieldName;
      options.ValidateAuthAppIDUrl = backendOptions.ValidateAuthAppIDUrl;
      options.ValidateAuthAppIDHeader = backendOptions.ValidateAuthAppIDHeader;
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
  private static Dictionary<int, int> KVIntPairs(List<string> list)
  {
      Dictionary<int, int> keyValuePairs = [];

      foreach (var item in list) {
          var kvp = item.Split(':');
          if (int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value)) {
              keyValuePairs.Add(key, value);
          } else {
              Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
          }
      }

      return keyValuePairs;
  }

  // Converts a List<string> to a dictionary of stgrings.
  private static Dictionary<string, string> KVStringPairs(List<string> list)
  {
      Dictionary<string, string> keyValuePairs = [];

      foreach (var item in list) {
          var kvp = item.Split(':');
          if (kvp.Length == 2) {
              keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
          } else{
              Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
          }
      }

      return keyValuePairs;
  }

  // Converts a comma-separated string to a list of strings.
  private static List<string> ToListOfString(string s)
  {
    if (String.IsNullOrEmpty(s))
      return [];

    return [.. s.Split(',').Select(p => p.Trim())];
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
      DisallowedHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("DisallowedHeaders", "")),
      HostName = ReadEnvironmentVariableOrDefault("Hostname", replicaID),
      Hosts = new List<BackendHostConfig>(),
      IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-",
      LogAllRequestHeaders = ReadEnvironmentVariableOrDefault("LogAllRequestHeaders", false),
      LogAllRequestHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllRequestHeadersExcept", "Authorization")),
      LogAllResponseHeaders = ReadEnvironmentVariableOrDefault("LogAllResponseHeaders", false),
      LogAllResponseHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllResponseHeadersExcept", "Api-Key")),
      LogHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("LogHeaders", "")),
      LogProbes = ReadEnvironmentVariableOrDefault("LogProbes", false),
      LookupHeaderName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "userId"),
      MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
      OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
      Port = ReadEnvironmentVariableOrDefault("Port", 80),
      PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
      PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
      PriorityKeyHeader = ReadEnvironmentVariableOrDefault("PriorityKeyHeader", "S7PPriorityKey"),
      PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
      PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
      RequiredHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("RequiredHeaders", "")),
      SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
      SuspendedUserConfigUrl = ReadEnvironmentVariableOrDefault("SuspendedUserConfigUrl", "file:config.json"),
      Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
      TimeoutHeader = ReadEnvironmentVariableOrDefault("TimeoutHeader", "S7PTimeout"),
      TerminationGracePeriodSeconds = ReadEnvironmentVariableOrDefault("TERMINATION_GRACE_PERIOD_SECONDS", 30),
      TTLHeader = ReadEnvironmentVariableOrDefault("TTLHeader", "S7PTTL"),
      UniqueUserHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("UniqueUserHeaders", "X-UserID")),
      UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", false),
      UseOAuthGov = ReadEnvironmentVariableOrDefault("UseOAuthGov", false),
      UserProfileHeader = ReadEnvironmentVariableOrDefault("UserProfileHeader", "X-UserProfile"),
      UseProfiles = ReadEnvironmentVariableOrDefault("UseProfiles", false),
      UserConfigUrl = ReadEnvironmentVariableOrDefault("UserConfigUrl", "file:config.json"),
      UserPriorityThreshold = ReadEnvironmentVariableOrDefault("UserPriorityThreshold", 0.1f),
      PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", "2:1,3:1"))),
      ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault("ValidateHeaders", ""))),
      ValidateAuthAppID = ReadEnvironmentVariableOrDefault("ValidateAuthAppID", false),
      ValidateAuthAppFieldName = ReadEnvironmentVariableOrDefault("ValidateAuthAppFieldName", "authAppID"),
      ValidateAuthAppIDUrl = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDUrl", "file:auth.json"),
      ValidateAuthAppIDHeader = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDHeader", "X-MS-CLIENT-PRINCIPAL-ID"),
      Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
    };

    //backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);
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

    // if (backendOptions.UniqueUserHeaders.Count > 0)
    // {
    //   // Make sure that uniqueUserHeaders are also in the required headers
    //   foreach (var header in backendOptions.UniqueUserHeaders)
    //   {
    //     if (!backendOptions.RequiredHeaders.Contains(header))
    //     {
    //       Console.WriteLine($"Adding {header} to RequiredHeaders");
    //       backendOptions.RequiredHeaders.Add(header);
    //     }
    //   }
    // }
    
    // If validate headers are set, make sure they are also in the required headers and disallowed headers
    if (backendOptions.ValidateHeaders.Count > 0)
    {
        foreach (var (key, value)  in backendOptions.ValidateHeaders) {
            Console.WriteLine($"Validating {key} against {value}");
            if (!backendOptions.RequiredHeaders.Contains(key)) {
                Console.WriteLine($"Adding {key} to RequiredHeaders");
                backendOptions.RequiredHeaders.Add(key);
            }
            if (!backendOptions.RequiredHeaders.Contains(value)) {
                Console.WriteLine($"Adding {value} to RequiredHeaders");
                backendOptions.RequiredHeaders.Add(value);
            }
            if (!backendOptions.DisallowedHeaders.Contains(value)) {
                Console.WriteLine($"Adding {value} to DisallowedHeaders");
                backendOptions.DisallowedHeaders.Add(value);
            }
        }
    }



    return backendOptions;
  }


}
