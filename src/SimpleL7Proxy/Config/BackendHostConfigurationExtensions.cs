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
using System.Reflection;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Config;

public static class BackendHostConfigurationExtensions
{
  private static ILogger? _logger;
  static Dictionary<string, string> EnvVars = new Dictionary<string, string>();


  public static BackendOptions CreateBackendOptions(ILogger logger)
  {
    _logger = logger;
    return LoadBackendOptions();
  }

  public static IServiceCollection AddBackendHostConfiguration(this IServiceCollection services, ILogger logger, BackendOptions backendOptions)
  {
    _logger = logger;

    services.AddSingleton(backendOptions); // Direct singleton
    services.Configure<BackendOptions>(opt =>
    {
      // Copy all properties from backendOptions to opt
      foreach (var prop in typeof(BackendOptions).GetProperties())
      {
        if (prop.CanWrite && prop.CanRead)
          prop.SetValue(opt, prop.GetValue(backendOptions));
      }
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

    // Treat placeholder as unset
    if (envValue == ConfigOptions.DefaultPlaceholder) envValue = null;

    // Use default if neither variable is defined
    string result = !string.IsNullOrEmpty(envValue) ? envValue : defaultValue;

    // Record and return the value
    EnvVars[variableName] = result;
    return result;
  }

  private static IterationModeEnum ReadEnvironmentVariableOrDefault(string variableName, IterationModeEnum defaultValue)
  {
    string? envValue = Environment.GetEnvironmentVariable(variableName)?.Trim();
    if (string.IsNullOrEmpty(envValue) || envValue == ConfigOptions.DefaultPlaceholder
        || !Enum.TryParse(envValue, out IterationModeEnum value))
    {
      EnvVars[variableName] = defaultValue.ToString();
      return defaultValue;
    }
    EnvVars[variableName] = value.ToString();
    return value;
  }

  private static bool ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
  {
    bool value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
    EnvVars[variableName] = value.ToString();
    return value;
  }

  // Reusable DataTable for evaluating simple arithmetic expressions (e.g. "60*10", "1200/2")
  private static readonly System.Data.DataTable s_mathTable = new();

  // Tries to evaluate a simple arithmetic expression (supports +, -, *, /).
  // Returns false if the expression is not valid math.
  private static bool TryEvaluateMathExpression(string expression, out double result)
  {
    result = 0;
    if (string.IsNullOrWhiteSpace(expression)) return false;
    try
    {
      var computed = s_mathTable.Compute(expression, null);
      result = Convert.ToDouble(computed);
      return true;
    }
    catch { return false; }
  }

  // Reads an environment variable and returns its value as an integer.
  // If the environment variable is not set, it returns the provided default value.
  // Supports simple arithmetic expressions (e.g. "60*10").
  private static int _ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
  {
    var envValue = Environment.GetEnvironmentVariable(variableName);
    if (envValue?.Trim() == ConfigOptions.DefaultPlaceholder) envValue = null;
    if (!int.TryParse(envValue, out var value))
    {
      // Try evaluating as a math expression (e.g. "60*10")
      if (TryEvaluateMathExpression(envValue!, out var mathResult))
        return (int)mathResult;
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
    if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
    {
      //_logger?.LogWarning($"Using default: {variableName}: {string.Join(",", defaultValues)}");
      return defaultValues;
    }
    try
    {
      // Strip JSON-style square brackets (e.g. "[200, 202, 401]" → "200, 202, 401")
      var trimmed = envValue.Trim();
      if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        trimmed = trimmed[1..^1];

      return trimmed.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
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
    if (envValue?.Trim() == ConfigOptions.DefaultPlaceholder) envValue = null;
    if (!float.TryParse(envValue, out var value))
    {
      // Try evaluating as a math expression (e.g. "0.5*2")
      if (TryEvaluateMathExpression(envValue!, out var mathResult))
        return (float)mathResult;
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
    if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
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
    if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
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

  // Converts a List<string> to a dictionary of strings.
  private static Dictionary<string, string> KVStringPairs(List<string> list, char delimiter = '=')
  {
    // Alternate delimiter to try when the primary doesn't produce a valid split
    char fallback = delimiter == '=' ? ':' : '=';
    Dictionary<string, string> keyValuePairs = [];

    foreach (var item in list)
    {
      // Split into max 2 parts so values containing the delimiter are preserved
      var kvp = item.Split(delimiter, 2);
      if (kvp.Length == 2)
      {
        keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
      }
      else
      {
        // Try the fallback delimiter (supports both '=' and ':' formats)
        kvp = item.Split(fallback, 2);
        if (kvp.Length == 2)
        {
          keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
        }
        else
        {
          Console.WriteLine($"Could not parse {item} as a key-value pair (delimiter='{delimiter}' or '{fallback}'), ignoring");
        }
      }
    }

    return keyValuePairs;
  }

  // Converts a comma-separated string to a list of strings.
  private static List<string> ToListOfString(string s)
  {
    if (String.IsNullOrEmpty(s))
      return [];

    // Strip JSON-style square brackets (e.g. "[a, b, c]" → "a, b, c")
    var trimmed = s.Trim();
    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
      trimmed = trimmed[1..^1];

    return [.. trimmed.Split(',').Select(p => p.Trim())];
  }

  // Converts a comma-separated string to a list of strings.
  private static string[] ToArrayOfString(string s)
  {
    if (String.IsNullOrEmpty(s))
      return Array.Empty<string>();

    // Strip JSON-style square brackets (e.g. "[a, b, c]" → "a, b, c")
    var trimmed = s.Trim();
    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
      trimmed = trimmed[1..^1];

    return trimmed.Split(',').Select(p => p.Trim()).ToArray();
  }

  // Converts a comma-separated string to a list of integers.
  private static List<int> ToListOfInt(string s)
  {
    if (String.IsNullOrEmpty(s))
      return new List<int>();

    // Strip JSON-style square brackets (e.g. "[1, 2, 3]" → "1, 2, 3")
    var trimmed = s.Trim();
    if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
      trimmed = trimmed[1..^1];

    return trimmed.Split(',').Select(p => int.Parse(p.Trim())).ToList();
  }

  // Generic configuration parser that supports both key=value pairs (order-independent) and legacy positional format
  // Returns a dictionary of parsed values
  private static Dictionary<string, string> ParseConfigString(string config, Dictionary<string, string[]> keyAliases, string configName)
  {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    if (string.IsNullOrEmpty(config))
      return result;

    var parts = config.Split(',').Select(p => p.Trim()).ToArray();

    // Check if it's the new key=value format
    if (parts.Length > 0 && parts[0].Contains('='))
    {
      // Parse as key=value pairs
      foreach (var part in parts)
      {
        var kvp = part.Split('=', 2); // Split into max 2 parts to handle = in connection strings/URIs
        if (kvp.Length == 2)
        {
          var key = kvp[0].Trim().ToLower();
          var value = kvp[1].Trim();

          // Find the canonical key name from aliases
          string? canonicalKey = null;
          foreach (var (canonical, aliases) in keyAliases)
          {
            if (aliases.Any(alias => alias.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
              canonicalKey = canonical;
              break;
            }
          }

          if (canonicalKey != null)
          {
            result[canonicalKey] = value;
          }
          else
          {
            _logger?.LogWarning($"Unknown {configName} key: {key}");
          }
        }
        else
        {
          _logger?.LogWarning($"Invalid {configName} key:value pair: {part}");
        }
      }
    }

    return result;
  }

  // Parses a comma-separated Service Bus configuration string into individual components
  // Format: "key1:value1,key2:value2,..." (order-independent)
  // Keys: connectionString (or cs), namespace (or ns), queue (or q), useMI (or mi)
  // Example: "cs:Endpoint=sb://...,ns:mysbnamespace,q:myqueue,mi:true"
  // Legacy format also supported: "connectionString,namespace,queue,useMI" (positional, must be in order)
  private static (string connectionString, string namespace_, string queue, bool useMI) ParseServiceBusConfig(string config)
  {
    // Fallback defaults — should match BackendOptions property initializers
    string connectionString = "example-sb-connection-string";
    string namespace_ = "";
    string queue = "requeststatus";
    bool useMI = false;

    if (string.IsNullOrEmpty(config))
      return (connectionString, namespace_, queue, useMI);

    var parts = config.Split(',').Select(p => p.Trim()).ToArray();

    // Check if it's the new key=value format
    if (parts.Length > 0 && parts[0].Contains('='))
    {
      // Use generic parser
      var keyAliases = new Dictionary<string, string[]>
      {
        { "connectionString", new[] { "connectionstring", "cs" } },
        { "namespace", new[] { "namespace", "ns" } },
        { "queue", new[] { "queue", "q" } },
        { "useMI", new[] { "usemi", "mi" } }
      };

      var parsed = ParseConfigString(config, keyAliases, "AsyncSBConfig");

      if (parsed.TryGetValue("connectionString", out var cs)) connectionString = cs;
      if (parsed.TryGetValue("namespace", out var ns)) namespace_ = ns;
      if (parsed.TryGetValue("queue", out var q)) queue = q;
      if (parsed.TryGetValue("useMI", out var mi)) useMI = mi.Equals("true", StringComparison.OrdinalIgnoreCase);

      return (connectionString, namespace_, queue, useMI);
    }
    else
    {
      // Legacy positional format: "connectionString,namespace,queue,useMI"
      if (parts.Length != 4)
      {
        _logger?.LogWarning($"ServiceBusConfig must have exactly 4 comma-separated values (connectionString,namespace,queue,useMI). Found {parts.Length} values. Using defaults.");
        return (connectionString, namespace_, queue, useMI);
      }

      useMI = parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
      return (parts[0], parts[1], parts[2], useMI);
    }
  }

  // Parses a comma-separated Blob Storage configuration string into individual components
  // Format: "key1=value1,key2=value2,..." (order-independent)
  // Keys: connectionString (or cs), accountUri (or uri), useMI (or mi)
  // Example: "uri:https://mystorageaccount.blob.core.windows.net/,mi:true"
  // Legacy format also supported: "connectionString,accountUri,useMI" (positional, must be in order)
  private static (string connectionString, string accountUri, bool useMI) ParseBlobStorageConfig(string config)
  {
    // Fallback defaults — should match BackendOptions property initializers
    string connectionString = "";
    string accountUri = "https://mystorageaccount.blob.core.windows.net/";
    bool useMI = false;

    if (string.IsNullOrEmpty(config))
      return (connectionString, accountUri, useMI);

    var parts = config.Split(',').Select(p => p.Trim()).ToArray();

    // Check if it's the new key=value format
    if (parts.Length > 0 && parts[0].Contains('='))
    {
      // Use generic parser
      var keyAliases = new Dictionary<string, string[]>
      {
        { "connectionString", new[] { "connectionstring", "cs" } },
        { "accountUri", new[] { "accounturi", "uri" } },
        { "useMI", new[] { "usemi", "mi" } }
      };

      var parsed = ParseConfigString(config, keyAliases, "AsyncBlobStorageConfig");

      if (parsed.TryGetValue("connectionString", out var cs)) connectionString = cs;
      if (parsed.TryGetValue("accountUri", out var uri)) accountUri = uri;
      if (parsed.TryGetValue("useMI", out var mi)) useMI = mi.Equals("true", StringComparison.OrdinalIgnoreCase);

      return (connectionString, accountUri, useMI);
    }
    else
    {
      // Legacy positional format: "connectionString,accountUri,useMI"
      if (parts.Length != 3)
      {
        _logger?.LogWarning($"AsyncBlobStorageConfig must have exactly 3 comma-separated values (connectionString,accountUri,useMI). Found {parts.Length} values. Using defaults.");
        return (connectionString, accountUri, useMI);
      }

      useMI = parts[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
      return (parts[0], parts[1], useMI);
    }
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

  // Sets a single BackendOptions property from an environment variable, dispatching
  // to the correct ReadEnvironmentVariableOrDefault overload based on the property type.
  private static void ApplyFieldFromEnv(BackendOptions target, BackendOptions defaults, string envVar, string property)
  {
    var pi = typeof(BackendOptions).GetProperty(property)!;
    var defVal = pi.GetValue(defaults);
    var type = pi.PropertyType;

    if (type == typeof(int) || type == typeof(double))
    {
      var val = ReadEnvironmentVariableOrDefault(envVar, Convert.ToInt32(defVal));
      pi.SetValue(target, Convert.ChangeType(val, type));
    }
    else if (type == typeof(float))
    {
      var val = ReadEnvironmentVariableOrDefault(envVar, Convert.ToSingle(defVal));
      pi.SetValue(target, Convert.ChangeType(val, type));
    }
    else if (type == typeof(string))
      pi.SetValue(target, ReadEnvironmentVariableOrDefault(envVar, (string)defVal!));
    else if (type == typeof(bool))
      pi.SetValue(target, ReadEnvironmentVariableOrDefault(envVar, (bool)defVal!));
    else if (type == typeof(List<string>))
      pi.SetValue(target, ToListOfString(ReadEnvironmentVariableOrDefault(envVar, string.Join(",", (List<string>)defVal!))));
    else
      throw new NotSupportedException($"ApplyFieldFromEnv: unsupported property type {type.Name} for {property}");
  }

  // Environment variable → BackendOptions property mappings for simple typed fields.
  // ApplyFieldFromEnv dispatches to the correct reader based on the property's runtime type.
  private static readonly (string envVar, string property)[] SimpleFields = [
      // int / double
      ("AsyncBlobWorkerCount", "AsyncBlobWorkerCount"),
      ("AsyncTimeout", "AsyncTimeout"),
      ("AsyncTTLSecs", "AsyncTTLSecs"),
      ("AsyncTriggerTimeout", "AsyncTriggerTimeout"),
      ("CBErrorThreshold", "CircuitBreakerErrorThreshold"),
      ("CBTimeslice", "CircuitBreakerTimeslice"),
      ("DefaultPriority", "DefaultPriority"),
      ("DefaultTTLSecs", "DefaultTTLSecs"),
      ("MaxQueueLength", "MaxQueueLength"),
      ("MaxAttempts", "MaxAttempts"),
      ("PollInterval", "PollInterval"),
      ("PollTimeout", "PollTimeout"),
      ("Port", "Port"),
      ("TERMINATION_GRACE_PERIOD_SECONDS", "TerminationGracePeriodSeconds"),
      ("Timeout", "Timeout"),
      ("UserConfigRefreshIntervalSecs", "UserConfigRefreshIntervalSecs"),
      ("UserSoftDeleteTTLMinutes", "UserSoftDeleteTTLMinutes"),
      ("Workers", "Workers"),
      // float
      ("SuccessRate", "SuccessRate"),
      ("UserPriorityThreshold", "UserPriorityThreshold"),
      // string
      ("AsyncClientRequestHeader", "AsyncClientRequestHeader"),
      ("AsyncClientConfigFieldName", "AsyncClientConfigFieldName"),
      ("CONTAINER_APP_NAME", "ContainerApp"),
      ("HealthProbeSidecar", "HealthProbeSidecar"),
      ("LoadBalanceMode", "LoadBalanceMode"),
      ("OAuthAudience", "OAuthAudience"),
      ("PriorityKeyHeader", "PriorityKeyHeader"),
      ("CONTAINER_APP_REVISION", "Revision"),
      ("StorageDbContainerName", "StorageDbContainerName"),
      ("SuspendedUserConfigUrl", "SuspendedUserConfigUrl"),
      ("TimeoutHeader", "TimeoutHeader"),
      ("TTLHeader", "TTLHeader"),
      ("UserConfigUrl", "UserConfigUrl"),
      ("UserProfileHeader", "UserProfileHeader"),
      ("ValidateAuthAppFieldName", "ValidateAuthAppFieldName"),
      ("ValidateAuthAppID", "ValidateAuthAppID"),
      ("ValidateAuthAppIDHeader", "ValidateAuthAppIDHeader"),
      ("ValidateAuthAppIDUrl", "ValidateAuthAppIDUrl"),
      // bool
      ("AsyncModeEnabled", "AsyncModeEnabled"),
      ("LogAllRequestHeaders", "LogAllRequestHeaders"),
      ("LogAllResponseHeaders", "LogAllResponseHeaders"),
      ("LogConsole", "LogConsole"),
      ("LogConsoleEvent", "LogConsoleEvent"),
      ("LogPoller", "LogPoller"),
      ("LogProbes", "LogProbes"),
      ("StorageDbEnabled", "StorageDbEnabled"),
      ("UseOAuth", "UseOAuth"),
      ("UseOAuthGov", "UseOAuthGov"),
      ("UseProfiles", "UseProfiles"),
      ("UserConfigRequired", "UserConfigRequired"),
      // List<string>
      ("DependancyHeaders", "DependancyHeaders"),
      ("DisallowedHeaders", "DisallowedHeaders"),
      ("LogAllRequestHeadersExcept", "LogAllRequestHeadersExcept"),
      ("LogAllResponseHeadersExcept", "LogAllResponseHeadersExcept"),
      ("LogHeaders", "LogHeaders"),
      ("PriorityKeys", "PriorityKeys"),
      ("RequiredHeaders", "RequiredHeaders"),
      ("StripRequestHeaders", "StripRequestHeaders"),
      ("StripResponseHeaders", "StripResponseHeaders"),
      ("UniqueUserHeaders", "UniqueUserHeaders"),
    ];

  // Loads backend options from environment variables or uses default values if the variables are not set.
  // It also configures the DNS refresh timeout and sets up an HttpClient instance.
  // If the IgnoreSSLCert environment variable is set to true, it configures the HttpClient to ignore SSL certificate errors.
  // If the AppendHostsFile environment variable is set to true, it appends the IP addresses and hostnames to the /etc/hosts file.
  private static BackendOptions LoadBackendOptions()
  {
    // Wait for the bootstrap App Configuration download (started in Main) and
    // push values into environment variables so every
    // ReadEnvironmentVariableOrDefault call below picks them up.
    var appConfigSettings = AppConfigBootstrap.WaitForDownload();
    if (appConfigSettings != null)
    {
      foreach (var kvp in appConfigSettings)
      {
        // strip  ["  and  "] from keys and values if present to support both raw and JSON-style formats
        string key = kvp.Key.Trim().TrimStart('[').TrimEnd(']').TrimStart('"').TrimEnd('"');
        string value = kvp.Value.Trim().TrimStart('[').TrimEnd(']').TrimStart('"').TrimEnd('"');
        Environment.SetEnvironmentVariable(key, value);
        Console.WriteLine($"[BOOTSTRAP] Set environment variable from App Configuration: {key}={value}");
      }
      _logger?.LogInformation("[BOOTSTRAP] Applied {Count} App Configuration value(s) as environment variables", appConfigSettings.Count);
    }

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

    // set timeout to large to disable it at HttpClient level.  Will use token cancellation for timeout instead.
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

    var defOpts = new BackendOptions(); // Create a default options object to get default values for individual settings

    // Create and return a BackendOptions object populated with values from environment variables or default values.
    // defOpts provides the single-source-of-truth defaults from BackendOptions property initializers.
    var backendOptions = new BackendOptions
    {
      Client = _client,
      Hosts = new List<HostConfig>(),
      AcceptableStatusCodes = ReadEnvironmentVariableOrDefault("AcceptableStatusCodes", defOpts.AcceptableStatusCodes),
      IterationMode = ReadEnvironmentVariableOrDefault("IterationMode", defOpts.IterationMode),
      PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", string.Join(",", defOpts.PriorityValues))),
      PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", string.Join(",", defOpts.PriorityWorkers.Select(kv => $"{kv.Key}:{kv.Value}"))))),
      UserIDFieldName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "UserIDFieldName", defOpts.UserIDFieldName), // migrate from LookupHeaderName
      ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault("ValidateHeaders", string.Join(",", defOpts.ValidateHeaders.Select(kv => $"{kv.Key}={kv.Value}"))))),
    };

    // RegisterBackends will be called after DI container is built to avoid service provider dependency issues

    // Apply all simple typed fields from environment variables via reflection
    foreach (var (envVar, property) in SimpleFields)
    {
      ApplyFieldFromEnv(backendOptions, defOpts, envVar, property);
    }

    // Apply settings with unique patterns (composite config overrides, computed identity)
    ApplyAsyncServiceBusOverrides(EnvVars, backendOptions, defOpts);
    ApplyAsyncBlobStorageOverrides(EnvVars, backendOptions, defOpts);
    ApplyReplicaIdentitySettings(EnvVars, backendOptions, replicaID);

    ValidatePrioritySettings(EnvVars, backendOptions, defOpts);
    ParseHealthProbeSidecarSettings(EnvVars, backendOptions, defOpts);
    ValidateHeaderSettings(EnvVars, backendOptions, defOpts);
    ValidateLoadBalanceMode(EnvVars, backendOptions, defOpts);

    OutputEnvVars();

    return backendOptions;
  }

  public static void RegisterBackends(BackendOptions backendOptions)
  {
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
        _logger?.LogDebug($"Found host {hostname} with probe path {probePath} and IP {ip}");

        // Resolve HostConfig from DI using the factory
        HostConfig bh = new HostConfig(hostname, probePath, ip, backendOptions.OAuthAudience);
        backendOptions.Hosts.Add(bh);

        sb.AppendLine($"{ip} {bh.Host}");
      }

      catch (UriFormatException e)
      {
        _logger?.LogError($"Could not add Host{i} with {hostname} : {e.Message}");
        Console.WriteLine(e.StackTrace);
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
  }

  private static void ApplyAsyncServiceBusOverrides(Dictionary<string, string> envVars, BackendOptions opts, BackendOptions defOpts)
  {
    // Parse composite config string, then allow individual env vars to override
    var configStr = ReadEnvironmentVariableOrDefault("AsyncSBConfig", defOpts.AsyncSBConfig);
    var (connStr, namespace_, queue, useMI) = ParseServiceBusConfig(configStr);
    opts.AsyncSBConfig = configStr;
    opts.AsyncSBConnectionString = ReadEnvironmentVariableOrDefault("AsyncSBConnectionString", connStr);
    opts.AsyncSBNamespace = ReadEnvironmentVariableOrDefault("AsyncSBNamespace", namespace_);
    opts.AsyncSBQueue = ReadEnvironmentVariableOrDefault("AsyncSBQueue", queue);
    opts.AsyncSBUseMI = ReadEnvironmentVariableOrDefault("AsyncSBUseMI", useMI);
  }

  private static void ApplyAsyncBlobStorageOverrides(Dictionary<string, string> envVars, BackendOptions opts, BackendOptions defOpts)
  {
    // Parse composite config string, then allow individual env vars to override
    var configStr = ReadEnvironmentVariableOrDefault("AsyncBlobStorageConfig", defOpts.AsyncBlobStorageConfig);
    var (connStr, accountUri, useMI) = ParseBlobStorageConfig(configStr);
    opts.AsyncBlobStorageConfig = configStr;
    opts.AsyncBlobStorageAccountUri = ReadEnvironmentVariableOrDefault("AsyncBlobStorageAccountUri", accountUri);
    opts.AsyncBlobStorageConnectionString = ReadEnvironmentVariableOrDefault("AsyncBlobStorageConnectionString", connStr);
    opts.AsyncBlobStorageUseMI = ReadEnvironmentVariableOrDefault("AsyncBlobStorageUseMI", useMI);
  }

  private static void ApplyReplicaIdentitySettings(Dictionary<string, string> envVars, BackendOptions opts, string replicaID)
  {
    opts.HostName = ReadEnvironmentVariableOrDefault("Hostname", replicaID);
    opts.IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-";
  }

  private static void ValidatePrioritySettings(Dictionary<string, string> envVars, BackendOptions backendOptions, BackendOptions defOpts)
  {
    // confirm the number of priority keys and values match
    if (backendOptions.PriorityKeys.Count != backendOptions.PriorityValues.Count)
    {
      Console.WriteLine($"The number of PriorityKeys and PriorityValues do not match in length, defaulting all values to {defOpts.DefaultPriority}");
      backendOptions.PriorityValues = Enumerable.Repeat(defOpts.DefaultPriority, backendOptions.PriorityKeys.Count).ToList();
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
  }

  private static void ParseHealthProbeSidecarSettings(Dictionary<string, string> envVars, BackendOptions backendOptions, BackendOptions defOpts)
  {
    var healthSettings = backendOptions.HealthProbeSidecar.Split(';', StringSplitOptions.RemoveEmptyEntries);
    foreach (var setting in healthSettings)
    {
      var kvp = setting.Split('=', 2);
      if (kvp.Length == 2)
      {
        var key = kvp[0].Trim().ToLower();
        var value = kvp[1].Trim().ToLower();
        if (key == "enabled")
        {
          backendOptions.HealthProbeSidecarEnabled = value == "true";
        }
        else if (key == "url" && !string.IsNullOrEmpty(value))
        {
          backendOptions.HealthProbeSidecarUrl = value;
        }
      }
    }
  }

  private static void ValidateHeaderSettings(Dictionary<string, string> envVars, BackendOptions backendOptions, BackendOptions defOpts)
  {
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
  }

  private static void ValidateLoadBalanceMode(Dictionary<string, string> envVars, BackendOptions backendOptions, BackendOptions defOpts)
  {
    backendOptions.LoadBalanceMode = backendOptions.LoadBalanceMode.Trim().ToLower();
    if (backendOptions.LoadBalanceMode != Constants.Latency &&
        backendOptions.LoadBalanceMode != Constants.RoundRobin &&
        backendOptions.LoadBalanceMode != Constants.Random)
    {
      Console.WriteLine($"Invalid LoadBalanceMode: {backendOptions.LoadBalanceMode}. Defaulting to '{defOpts.LoadBalanceMode}'.");
      backendOptions.LoadBalanceMode = defOpts.LoadBalanceMode;
    }
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
