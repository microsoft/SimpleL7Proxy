using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging; 
using Microsoft.Extensions.Options;
using OS = System;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Config;

public static class BackendHostConfigurationExtensions
{
  private static ILogger? _logger;
  static Dictionary<string, string> EnvVars = new Dictionary<string, string>();


  public static IServiceCollection AddBackendHostConfiguration(this IServiceCollection services, ILogger logger)
  {
    var backendOptions = LoadBackendOptions();
    _logger = logger;

    services.Configure<BackendOptions>(options =>
    {
      options.AcceptableStatusCodes = backendOptions.AcceptableStatusCodes;
      options.AsyncBlobStorageAccountUri = backendOptions.AsyncBlobStorageAccountUri;
      options.AsyncBlobStorageConnectionString = backendOptions.AsyncBlobStorageConnectionString;
      options.AsyncBlobStorageUseMI = backendOptions.AsyncBlobStorageUseMI;
      options.AsyncClientRequestHeader = backendOptions.AsyncClientRequestHeader;
      options.AsyncClientConfigFieldName = backendOptions.AsyncClientConfigFieldName;
      options.AsyncModeEnabled = backendOptions.AsyncModeEnabled;
      options.AsyncSBConnectionString = backendOptions.AsyncSBConnectionString;
      options.AsyncSBNamespace = backendOptions.AsyncSBNamespace;
      options.AsyncSBQueue = backendOptions.AsyncSBQueue;
      options.AsyncSBUseMI = backendOptions.AsyncSBUseMI;
      options.AsyncTimeout = backendOptions.AsyncTimeout;
      options.AsyncTriggerTimeout = backendOptions.AsyncTriggerTimeout;
      options.CircuitBreakerErrorThreshold = backendOptions.CircuitBreakerErrorThreshold;
      options.CircuitBreakerTimeslice = backendOptions.CircuitBreakerTimeslice;
      options.Client = backendOptions.Client;
      options.ContainerApp = backendOptions.ContainerApp;
      options.DefaultPriority = backendOptions.DefaultPriority;
      options.DefaultTTLSecs = backendOptions.DefaultTTLSecs;
      options.DisallowedHeaders = backendOptions.DisallowedHeaders;
      options.HostName = backendOptions.HostName;
      options.Hosts = backendOptions.Hosts;
      options.IDStr = backendOptions.IDStr;
      options.LoadBalanceMode = backendOptions.LoadBalanceMode;
      options.LogAllRequestHeaders = backendOptions.LogAllRequestHeaders;
      options.LogAllRequestHeadersExcept = backendOptions.LogAllRequestHeadersExcept;
      options.LogAllResponseHeaders = backendOptions.LogAllResponseHeaders;
      options.LogAllResponseHeadersExcept = backendOptions.LogAllResponseHeadersExcept;
      options.LogConsole = backendOptions.LogConsole;
      options.LogConsoleEvent = backendOptions.LogConsoleEvent;
      options.LogHeaders = backendOptions.LogHeaders;
      options.LogPoller = backendOptions.LogPoller;
      options.LogProbes = backendOptions.LogProbes;
      options.UserIDFieldName = backendOptions.UserIDFieldName;
      options.MaxQueueLength = backendOptions.MaxQueueLength;
      options.OAuthAudience = backendOptions.OAuthAudience;
      options.PollInterval = backendOptions.PollInterval;
      options.PollTimeout = backendOptions.PollTimeout;
      options.Port = backendOptions.Port;
      options.PriorityKeyHeader = backendOptions.PriorityKeyHeader;
      options.PriorityKeys = backendOptions.PriorityKeys;
      options.PriorityValues = backendOptions.PriorityValues;
      options.PriorityWorkers = backendOptions.PriorityWorkers;
      options.RequiredHeaders = backendOptions.RequiredHeaders;
      options.Revision = backendOptions.Revision;
      options.SuccessRate = backendOptions.SuccessRate;
      options.SuspendedUserConfigUrl = backendOptions.SuspendedUserConfigUrl;
      options.StorageDbEnabled = backendOptions.StorageDbEnabled;
      options.StorageDbContainerName = backendOptions.StorageDbContainerName;
      options.StripHeaders = backendOptions.StripHeaders;
      options.TerminationGracePeriodSeconds = backendOptions.TerminationGracePeriodSeconds;
      options.Timeout = backendOptions.Timeout;
      options.TimeoutHeader = backendOptions.TimeoutHeader;
      options.TTLHeader = backendOptions.TTLHeader;
      options.UniqueUserHeaders = backendOptions.UniqueUserHeaders;
      options.UseOAuth = backendOptions.UseOAuth;
      options.UseOAuthGov = backendOptions.UseOAuthGov;
      options.UseProfiles = backendOptions.UseProfiles;
      options.UserConfigUrl = backendOptions.UserConfigUrl;
      options.UserConfigRequired = backendOptions.UserConfigRequired;
      options.UserPriorityThreshold = backendOptions.UserPriorityThreshold;
      options.UserProfileHeader = backendOptions.UserProfileHeader;
      options.ValidateAuthAppFieldName = backendOptions.ValidateAuthAppFieldName;
      options.ValidateAuthAppID = backendOptions.ValidateAuthAppID;
      options.ValidateAuthAppIDHeader = backendOptions.ValidateAuthAppIDHeader;
      options.ValidateAuthAppIDUrl = backendOptions.ValidateAuthAppIDUrl;
      options.ValidateHeaders = backendOptions.ValidateHeaders;
      options.Workers = backendOptions.Workers;
    });

    return services;
  }

  private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
  {
    int value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
    EnvVars[variableName] = value.ToString();
    return value;
  }

  private static int[] ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues)
  {
    int[] value = _ReadEnvironmentVariableOrDefault(variableName, defaultValues);
    EnvVars[variableName] = string.Join(",", value);
    return value;
  }
  private static float ReadEnvironmentVariableOrDefault(string variableName, float defaultValue)
  {
    float value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
    EnvVars[variableName] = value.ToString();
    return value;
  }
  private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
  {
    string value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
    EnvVars[variableName] = value;
    return value;
  }
  private static string ReadEnvironmentVariableOrDefault(string altVariableName, string variableName, string defaultValue)
  {
    // Try both variable names and use the first non-empty one
    string? envValue = Environment.GetEnvironmentVariable(variableName)?.Trim() ??
                       Environment.GetEnvironmentVariable(altVariableName)?.Trim();

    // Use default if neither variable is defined
    string result = !string.IsNullOrEmpty(envValue) ? envValue : defaultValue;

    // Record and return the value
    EnvVars[variableName] = result;
    return result;
  }

  private static bool ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
  {
    bool value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
    EnvVars[variableName] = value.ToString();
    return value;
  }

  // Reads an environment variable and returns its value as an integer.
  // If the environment variable is not set, it returns the provided default value.
  private static int _ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (!int.TryParse(envValue, out var value))
    {
      //_logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return value;
  }

  // Reads an environment variable and returns its value as an integer[].
  // If the environment variable is not set, it returns the provided default value.
  private static int[] _ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(envValue))
    {
      //_logger?.LogWarning($"Using default: {variableName}: {string.Join(",", defaultValues)}");
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
  private static float _ReadEnvironmentVariableOrDefault(string variableName, float defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (!float.TryParse(envValue, out var value))
    {
      //_logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return value;
  }
  // Reads an environment variable and returns its value as a string.
  // If the environment variable is not set, it returns the provided default value.
  private static string _ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (string.IsNullOrEmpty(envValue))
    {
      //_logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return envValue.Trim();
  }

  // Reads an environment variable and returns its value as a string.
  // If the environment variable is not set, it returns the provided default value.
  private static bool _ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
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

    foreach (var item in list)
    {
      var kvp = item.Split(':');
      if (int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value))
      {
        keyValuePairs.Add(key, value);
      }
      else
      {
        Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
      }
    }

    return keyValuePairs;
  }

  // Converts a List<string> to a dictionary of stgrings.
  private static Dictionary<string, string> KVStringPairs(List<string> list)
  {
    Dictionary<string, string> keyValuePairs = [];

    foreach (var item in list)
    {
      var kvp = item.Split(':');
      if (kvp.Length == 2)
      {
        keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
      }
      else
      {
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

    // Converts a comma-separated string to a list of strings.
  private static string[] ToArrayOfString(string s)
  {
    if (String.IsNullOrEmpty(s))
      return Array.Empty<string>();

    return s.Split(',').Select(p => p.Trim()).ToArray();
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

  private static SocketsHttpHandler getHandler(int initialDelaySecs, int IntervalSecs, int linuxRetryCount)
  {
    SocketsHttpHandler handler = new SocketsHttpHandler();
    handler.ConnectCallback = async (ctx, ct) =>
    {
      DnsEndPoint dnsEndPoint = ctx.DnsEndPoint;
      IPAddress[] addresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, dnsEndPoint.AddressFamily, ct).ConfigureAwait(false);
      var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
      try
      {
        bool linuxKeepAliveConfigured = false;

        // Basic keep-alive setting - should work on all platforms
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
          if (OperatingSystem.IsWindows())
          {
            // Windows-specific approach using IOControl
            byte[] keepAliveValues = new byte[12];
            BitConverter.GetBytes((uint)1).CopyTo(keepAliveValues, 0);           // Turn keep-alive on
            BitConverter.GetBytes((uint)60000).CopyTo(keepAliveValues, initialDelaySecs);       // 60 seconds before first keep-alive
            BitConverter.GetBytes((uint)30000).CopyTo(keepAliveValues, IntervalSecs);       // 30 second interval

            s.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
            //Console.WriteLine("TCP keep-alive settings applied using Windows-specific method");
          }
          else if (OperatingSystem.IsLinux())
          {

            // Set keep-alive idle time in milliseconds
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, initialDelaySecs);

            // Set keep-alive interval in milliseconds
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, IntervalSecs);

            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, linuxRetryCount);
            linuxKeepAliveConfigured = true;

            //Console.WriteLine($"TCPKEEPALIVETIME set to {initialDelaySecs} seconds (connection idle time before sending probes)");
            //Console.WriteLine($"TCPKEEPALIVEINTERVAL set to {IntervalSecs} seconds (interval between probes)");
            // Console.WriteLine($"TCPKEEPALIVERETRYCOUNT set to {linuxRetryCount} probes (max failures before disconnect)");
          }

        }
        catch (Exception ex)
        {
          ProxyEvent pe = new()
          {
            Type = EventType.Exception,
            Exception = ex,
            ["Message"] = "Failed to set TCP keep-alive parameters",
            ["Host"] = dnsEndPoint.Host,
            ["Port"] = dnsEndPoint.Port.ToString(),
            ["InitialDelaySecs"] = initialDelaySecs.ToString(),
            ["IntervalSecs"] = IntervalSecs.ToString(),
            ["LinuxRetryCount"] = linuxRetryCount.ToString(),
            ["linuxKeepAliveConfigured"] = linuxKeepAliveConfigured.ToString()
          };
          pe.SendEvent();
        }

        // Connect to the endpoint
        await s.ConnectAsync(addresses, dnsEndPoint.Port, ct).ConfigureAwait(false);
        return new NetworkStream(s, ownsSocket: true);
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"Socket connection error: {ex.Message}");
        s.Dispose();
        throw;
      }
    };

    return handler;
  }

  // Loads backend options from environment variables or uses default values if the variables are not set.
  // It also configures the DNS refresh timeout and sets up an HttpClient instance.
  // If the IgnoreSSLCert environment variable is set to true, it configures the HttpClient to ignore SSL certificate errors.
  // If the AppendHostsFile environment variable is set to true, it appends the IP addresses and hostnames to the /etc/hosts file.
  private static BackendOptions LoadBackendOptions()
  {
    // Read and set the DNS refresh timeout from environment variables or use the default value
    var DNSTimeout = ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 240000);
    var KeepAliveInitialDelaySecs = ReadEnvironmentVariableOrDefault("KeepAliveInitialDelaySecs", 60); // 60 seconds
    var KeepAlivePingIntervalSecs = ReadEnvironmentVariableOrDefault("KeepAlivePingIntervalSecs", 60); // 60 seconds
    var keepAliveDurationSecs = ReadEnvironmentVariableOrDefault("KeepAliveIdleTimeoutSecs", 1200); // 20 minutes

    var EnableMultipleHttp2Connections = ReadEnvironmentVariableOrDefault("EnableMultipleHttp2Connections", false);
    var MultiConnLifetimeSecs = ReadEnvironmentVariableOrDefault("MultiConnLifetimeSecs", 3600); // 1 hours
    var MultiConnIdleTimeoutSecs = ReadEnvironmentVariableOrDefault("MultiConnIdleTimeoutSecs", 300); // 5 minutes
    var MultiConnMaxConns = ReadEnvironmentVariableOrDefault("MultiConnMaxConns", 4000); // 4000 connections

    var retryCount = keepAliveDurationSecs / KeepAlivePingIntervalSecs; // Calculate retry count 
    var handler = getHandler(KeepAliveInitialDelaySecs, KeepAlivePingIntervalSecs, retryCount);

    if (EnableMultipleHttp2Connections)
    {
      handler.EnableMultipleHttp2Connections = true;
      handler.PooledConnectionLifetime = TimeSpan.FromSeconds(MultiConnLifetimeSecs);
      handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(MultiConnIdleTimeoutSecs);
      handler.MaxConnectionsPerServer = MultiConnMaxConns;
      handler.ResponseDrainTimeout = TimeSpan.FromSeconds(keepAliveDurationSecs);
      Console.WriteLine("Multiple HTTP/2 connections enabled.");
    }
    else
    {
      handler.EnableMultipleHttp2Connections = false;
      Console.WriteLine("Multiple HTTP/2 connections disabled.");
    }
    //     PooledConnectionIdleTimeout = TimeSpan.FromSeconds(KeepAliveIdleTimeoutSecs),


    // Configure SSL handling
    if (ReadEnvironmentVariableOrDefault("IgnoreSSLCert", false))
    {
      handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
      {
        RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
      };
      Console.WriteLine("Ignoring SSL certificate validation errors.");
    }

    HttpClient _client = new HttpClient(handler);

    // set timeout to large ti disable it at HttpClient level.  Will use token cancellation for timeout instead.
    _client.Timeout = Timeout.InfiniteTimeSpan;

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
      AcceptableStatusCodes = ReadEnvironmentVariableOrDefault("AcceptableStatusCodes", new int[] { 200, 202, 401, 403, 404, 408, 410, 412, 417, 400 }),
      AsyncBlobStorageAccountUri = ReadEnvironmentVariableOrDefault("AsyncBlobStorageAccountUri", "https://example.blob.core.windows.net/"),
      AsyncBlobStorageConnectionString = ReadEnvironmentVariableOrDefault("AsyncBlobStorageConnectionString", ""),
      AsyncBlobStorageUseMI = ReadEnvironmentVariableOrDefault("AsyncBlobStorageUseMI", false),
      AsyncClientRequestHeader = ReadEnvironmentVariableOrDefault("AsyncClientRequestHeader", "AsyncMode"),
      AsyncClientConfigFieldName = ReadEnvironmentVariableOrDefault("AsyncClientConfigFieldName", "async-config"),
      AsyncModeEnabled = ReadEnvironmentVariableOrDefault("AsyncModeEnabled", false),
      AsyncSBConnectionString = ReadEnvironmentVariableOrDefault("AsyncSBConnectionString", "example-sb-connection-string"),
      AsyncSBNamespace = ReadEnvironmentVariableOrDefault("AsyncSBNamespace", ""),
      AsyncSBQueue = ReadEnvironmentVariableOrDefault("AsyncSBQueue", "requeststatus"),
      AsyncSBUseMI = ReadEnvironmentVariableOrDefault("AsyncSBUseMI", false), // Use managed identity for Service Bus
      AsyncTimeout = ReadEnvironmentVariableOrDefault("AsyncTimeout", 30 * 60000),
      AsyncTriggerTimeout = ReadEnvironmentVariableOrDefault("AsyncTriggerTimeout", 10000),
      CircuitBreakerErrorThreshold = ReadEnvironmentVariableOrDefault("CBErrorThreshold", 50),
      CircuitBreakerTimeslice = ReadEnvironmentVariableOrDefault("CBTimeslice", 60),
      Client = _client,
      ContainerApp = ReadEnvironmentVariableOrDefault("CONTAINER_APP_NAME", "ContainerAppName"),
      DefaultPriority = ReadEnvironmentVariableOrDefault("DefaultPriority", 2),
      DefaultTTLSecs = ReadEnvironmentVariableOrDefault("DefaultTTLSecs", 300),
      DependancyHeaders = ToArrayOfString(ReadEnvironmentVariableOrDefault("DependancyHeaders", "Backend-Host, Host-URL, Status, Duration, Error, Message, Request-Date, backendLog")),
      DisallowedHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("DisallowedHeaders", "")),
      HostName = ReadEnvironmentVariableOrDefault("Hostname", replicaID),
      Hosts = new List<BackendHostConfig>(),
      IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-",
      LoadBalanceMode = ReadEnvironmentVariableOrDefault("LoadBalanceMode", "latency"), // "latency", "roundrobin", "random"
      LogAllRequestHeaders = ReadEnvironmentVariableOrDefault("LogAllRequestHeaders", false),
      LogAllRequestHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllRequestHeadersExcept", "Authorization")),
      LogAllResponseHeaders = ReadEnvironmentVariableOrDefault("LogAllResponseHeaders", false),
      LogAllResponseHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllResponseHeadersExcept", "Api-Key")),
      LogConsole = ReadEnvironmentVariableOrDefault("LogConsole", true),
      LogConsoleEvent = ReadEnvironmentVariableOrDefault("LogConsoleEvent", false),
      LogHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("LogHeaders", "")),
      LogPoller = ReadEnvironmentVariableOrDefault("LogPoller", true),
      LogProbes = ReadEnvironmentVariableOrDefault("LogProbes", true),
      MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
      OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
      PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
      PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
      Port = ReadEnvironmentVariableOrDefault("Port", 80),
      PriorityKeyHeader = ReadEnvironmentVariableOrDefault("PriorityKeyHeader", "S7PPriorityKey"),
      PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
      PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
      PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", "2:1,3:1"))),
      RequiredHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("RequiredHeaders", "")),
      Revision = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REVISION", "revisionID"),
      SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
      SuspendedUserConfigUrl = ReadEnvironmentVariableOrDefault("SuspendedUserConfigUrl", "file:config.json"),
      StripHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("StripHeaders", "")),
      StorageDbEnabled = ReadEnvironmentVariableOrDefault("StorageDbEnabled", false),
      StorageDbContainerName = ReadEnvironmentVariableOrDefault("StorageDbContainerName", "Requests"),
      TerminationGracePeriodSeconds = ReadEnvironmentVariableOrDefault("TERMINATION_GRACE_PERIOD_SECONDS", 30),
      Timeout = ReadEnvironmentVariableOrDefault("Timeout", 1200000), // 20 minutes
      TimeoutHeader = ReadEnvironmentVariableOrDefault("TimeoutHeader", "S7PTimeout"),
      TTLHeader = ReadEnvironmentVariableOrDefault("TTLHeader", "S7PTTL"),
      UniqueUserHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("UniqueUserHeaders", "X-UserID")),
      UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", false),
      UseOAuthGov = ReadEnvironmentVariableOrDefault("UseOAuthGov", false),
      UseProfiles = ReadEnvironmentVariableOrDefault("UseProfiles", false),
      UserConfigUrl = ReadEnvironmentVariableOrDefault("UserConfigUrl", "file:config.json"),
      UserConfigRequired = ReadEnvironmentVariableOrDefault("UserConfigRequired", false),
      UserIDFieldName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "UserIDFieldName", "userId"), // migrate from LookupHeaderName
      UserPriorityThreshold = ReadEnvironmentVariableOrDefault("UserPriorityThreshold", 0.1f),
      UserProfileHeader = ReadEnvironmentVariableOrDefault("UserProfileHeader", "X-UserProfile"),
      ValidateAuthAppFieldName = ReadEnvironmentVariableOrDefault("ValidateAuthAppFieldName", "authAppID"),
      ValidateAuthAppID = ReadEnvironmentVariableOrDefault("ValidateAuthAppID", false),
      ValidateAuthAppIDHeader = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDHeader", "X-MS-CLIENT-PRINCIPAL-ID"),
      ValidateAuthAppIDUrl = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDUrl", "file:auth.json"),
      ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault("ValidateHeaders", ""))),
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
      foreach (var (key, value) in backendOptions.ValidateHeaders)
      {
        Console.WriteLine($"Validating {key} against {value}");
        if (!backendOptions.RequiredHeaders.Contains(key))
        {
          Console.WriteLine($"Adding {key} to RequiredHeaders");
          backendOptions.RequiredHeaders.Add(key);
        }
        if (!backendOptions.RequiredHeaders.Contains(value))
        {
          Console.WriteLine($"Adding {value} to RequiredHeaders");
          backendOptions.RequiredHeaders.Add(value);
        }
        if (!backendOptions.DisallowedHeaders.Contains(value))
        {
          Console.WriteLine($"Adding {value} to DisallowedHeaders");
          backendOptions.DisallowedHeaders.Add(value);
        }
      }
    }

    // Validate LoadBalanceMode case insensitively
    backendOptions.LoadBalanceMode = backendOptions.LoadBalanceMode.Trim().ToLower();
    if (backendOptions.LoadBalanceMode != Constants.Latency &&
        backendOptions.LoadBalanceMode != Constants.RoundRobin &&
        backendOptions.LoadBalanceMode != Constants.Random)
    {
      Console.WriteLine($"Invalid LoadBalanceMode: {backendOptions.LoadBalanceMode}. Defaulting to '{Constants.Latency}'.");
      backendOptions.LoadBalanceMode = Constants.Latency;
    }

    OutputEnvVars();

    return backendOptions;
  }

  private static void OutputEnvVars()
  {
    const int keyWidth = 27;
    const int valWidth = 30;
    const int gutterWidth = 4;
    int col = 0;
    string? pendingEntry = null;
    foreach (var kvp in EnvVars)
    {
      string key = kvp.Key;
      string value = kvp.Value;

      // Prepare the entry for this pair
      string entry = $"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}:" +
                  $"{(value.Length > valWidth ? value.Substring(0, valWidth - 3) + "..." : value),-valWidth}";

      if (col == 0)
      {
        // Store the first column entry and wait for the second
        pendingEntry = entry;
        col = 1;
      }
      else
      {
        // If the untrimmed key or value for the second column is too long, print it on its own line
        if (key.Length > keyWidth || value.Length > valWidth)
        {
          // Print the pending first column entry alone
          Console.WriteLine(pendingEntry);
          // Print the long second column entry alone, but obey key/value widths
          Console.WriteLine($"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}: {value}");
          pendingEntry = null;
          col = 0;
        }
        else
        {
          // Print both columns on the same line with gutter
          Console.WriteLine($"{pendingEntry}{new string(' ', gutterWidth)}{entry}");
          pendingEntry = null;
          col = 0;
        }
      }
    }
    if (col % 2 != 0)
    {
      Console.WriteLine();
    }
  }

}
