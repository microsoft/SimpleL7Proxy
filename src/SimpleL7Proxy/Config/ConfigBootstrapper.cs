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
using System.Collections;
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

public static class ConfigBootstrapper
{
  private static ILogger? _logger;
  static Dictionary<string, string> EnvVars = new Dictionary<string, string>();
  public static readonly BackendOptions s_defaults = new();


  public static BackendOptions CreateBackendOptions(ILogger logger, AppConfigBootstrap appConfigBootstrap)
  {
    Dictionary<string, string> effectiveEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    _logger = logger;

    foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
    {
      var key = de.Key?.ToString();
      if (string.IsNullOrEmpty(key))
        continue;
      effectiveEnvironment[key] = de.Value?.ToString() ?? string.Empty;
    }

    // Wait for the bootstrap App Configuration download (started in Main) and
    // add values into the effective environment dictionary so every
    // ReadEnvironmentVariableOrDefault call below picks them up.
    var appConfigSettings = appConfigBootstrap.WaitForDownload();
    if (appConfigSettings != null)
    {
      foreach (var kvp in appConfigSettings)
      {
        // Keys from AppConfigBootstrap are already plain config names (e.g. "DependancyHeaders").
        // Values may be JSON-style arrays like ["a","b"] — leave them intact;
        // downstream parsers (ToListOfString, ReadEnvironmentVariableOrDefault) 
        // already handle bracket/quote stripping correctly.
        string key = kvp.Key.Trim();
        string value = kvp.Value.Trim();
        effectiveEnvironment[key] = value;
      }
      _logger?.LogInformation("[BOOTSTRAP] Applied {Count} App Configuration value(s) to effective environment", appConfigSettings.Count);
    }
    var backendOptions = ConfigParser.ParseOptions(effectiveEnvironment);
    ConfigureHttpClientFromOptions(effectiveEnvironment, backendOptions);

    OutputEnvVars(backendOptions);

    return backendOptions;
  }

  private static void OutputEnvVars(BackendOptions backendOptions)
  {
    // Build Warm / Cold / Hidden buckets from [ConfigOption] attributes
    var warm = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var cold = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var hidden = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var prop in typeof(BackendOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      var attr = prop.GetCustomAttribute<ConfigOptionAttribute>();
      if (attr == null) continue;

      var rawValue = prop.GetValue(backendOptions);
      var value = FormatValue(rawValue);
      var display = MaskSensitive(attr.KeyPath, value);

      var bucket = attr.Mode switch
      {
        ConfigMode.Warm => warm,
        ConfigMode.Cold => cold,
        _ => hidden
      };
      bucket[$"{attr.Mode}:{attr.KeyPath}"] = display;
    }

    // generate a JSON representation for logging
    var all = warm.Concat(cold).Concat(hidden).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    string json = System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    // _logger?.LogInformation("Effective configuration:\n{ConfigJson}", json);

    static string MaskSensitive(string key, string value)
    {
      if (string.IsNullOrEmpty(value)) return value;

      var lower = key.ToLowerInvariant();
      if (lower.Contains("connectionstring") || lower.Contains("password") ||
          lower.Contains("secret") || lower.Contains("token") ||
          lower.Contains("apikey") || lower.Contains("sas"))
      {
        return value.Length <= 4 ? "****" : $"{value[..2]}***{value[^2..]}";
      }
      return value;
    }

    static string FormatValue(object? rawValue)
    {
      if (rawValue == null) return "";
      return rawValue switch
      {
        string s => s,
        int[] arr => string.Join(", ", arr),
        IEnumerable<string> list => string.Join(", ", list),
        IEnumerable<int> list => string.Join(", ", list),
        IDictionary<string, string> dict => string.Join(", ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}")),
        IDictionary<int, int> dict => string.Join(", ", dict.Select(kvp => $"{kvp.Key}:{kvp.Value}")),
        _ => rawValue.ToString() ?? ""
      };
    }
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

  private static int ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
  {
    int value = _ReadEnvironmentVariableOrDefault(env, variableName, defaultValue);
    EnvVars[variableName] = value.ToString();
    return value;
  }

  private static bool ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, bool defaultValue)
  {
    bool value = _ReadEnvironmentVariableOrDefault(env, variableName, defaultValue);
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
  private static int _ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
  {
    var envValue = env.GetValueOrDefault(variableName);
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

  // Reads an environment variable and returns its value as a string.
  // If the environment variable is not set, it returns the provided default value.
  private static bool _ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, bool defaultValue)
  {
    var envValue = env.GetValueOrDefault(variableName);
    if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
    {
      _logger?.LogWarning($"Using default: {variableName}: {defaultValue}");
      return defaultValue;
    }
    return envValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
  }

  // Converts a List<string> to a dictionary of integers.

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
            BitConverter.GetBytes((uint)(initialDelaySecs * 1000)).CopyTo(keepAliveValues, 4);
            BitConverter.GetBytes((uint)(IntervalSecs * 1000)).CopyTo(keepAliveValues, 8);

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

  // Activates runtime resources derived from config (HttpClient/transport) after parsing.
  private static void ConfigureHttpClientFromOptions(Dictionary<string, string> env, BackendOptions backendOptions)
  {
    // Read and set the DNS refresh timeout from environment variables or use the default value
    // var DNSTimeout = ReadEnvironmentVariableOrDefault(env, "DnsRefreshTimeout", 240000);
    var KeepAliveInitialDelaySecs = ReadEnvironmentVariableOrDefault(env, "KeepAliveInitialDelaySecs", 60); // 60 seconds
    var KeepAlivePingIntervalSecs = ReadEnvironmentVariableOrDefault(env, "KeepAlivePingIntervalSecs", 60); // 60 seconds
    var keepAliveDurationSecs = ReadEnvironmentVariableOrDefault(env, "KeepAliveIdleTimeoutSecs", 1200); // 20 minutes
    var safeKeepAliveInitialDelaySecs = Math.Max(1, KeepAliveInitialDelaySecs);
    var safeKeepAlivePingIntervalSecs = Math.Max(1, KeepAlivePingIntervalSecs);

    var EnableMultipleHttp2Connections = ReadEnvironmentVariableOrDefault(env, "EnableMultipleHttp2Connections", false);
    var MultiConnLifetimeSecs = ReadEnvironmentVariableOrDefault(env, "MultiConnLifetimeSecs", 3600); // 1 hours
    var MultiConnIdleTimeoutSecs = ReadEnvironmentVariableOrDefault(env, "MultiConnIdleTimeoutSecs", 300); // 5 minutes
    var MultiConnMaxConns = ReadEnvironmentVariableOrDefault(env, "MultiConnMaxConns", 4000); // 4000 connections

    var retryCount = Math.Max(1, keepAliveDurationSecs / safeKeepAlivePingIntervalSecs);
    var handler = getHandler(safeKeepAliveInitialDelaySecs, safeKeepAlivePingIntervalSecs, retryCount);

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

    // Configure SSL handling
    if (ReadEnvironmentVariableOrDefault(env, "IgnoreSSLCert", false))
    {
      handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
      {
        RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
      };
      Console.WriteLine("Ignoring SSL certificate validation errors.");
    }

    HttpClient client = new HttpClient(handler)
    {
      // set timeout to large to disable it at HttpClient level. Will use token cancellation for timeout instead.
      Timeout = Timeout.InfiniteTimeSpan
    };

    backendOptions.Client = client;
  }

  /// <summary>
  /// Clears and re-populates <see cref="BackendOptions.Hosts"/> by iterating
  /// over <c>Host1..N</c>, <c>Probe_path1..N</c>, and <c>IP1..N</c> keys.
  /// <para>
  /// Each parsed <see cref="HostConfig"/> is staged into <paramref name="hostCollection"/>
  /// (when provided). After all hosts are parsed, <see cref="IHostHealthCollection.Activate"/>
  /// is called to build, freeze, and swap the snapshot.
  /// </para>
  /// <para>
  /// Values are resolved in priority order:
  /// <paramref name="cfg"/> dictionary → <c>Warm:</c>/<c>Cold:</c>/bare-key
  /// from <paramref name="configuration"/> → environment variable.
  /// </para>
  /// <para>
  /// If <c>APPENDHOSTSFILE</c> is <c>"true"</c>, the resolved host/IP pairs
  /// are also appended to <c>/etc/hosts</c> (Linux container deployments).
  /// </para>
  /// </summary>
  /// <param name="backendOptions">The target options whose <c>Hosts</c> list will be rebuilt.</param>
  /// <param name="configuration">Optional <see cref="IConfiguration"/> for Warm/Cold/bare-key lookup.</param>
  /// <param name="cfg">
  /// Optional flat dictionary of host-family settings (e.g. from a warm snapshot).
  /// Takes precedence over <paramref name="configuration"/> when supplied.
  /// </param>
  /// <param name="hostCollection">
  /// Optional host collection manager. When provided, each parsed host is staged
  /// and the collection is activated at the end.
  /// </param>
  public static void RegisterBackends(BackendOptions backendOptions, IConfiguration? configuration = null, Dictionary<string, string>? cfg = null, IHostHealthCollection? hostCollection = null)
  {
    //backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);
    var hostSettingsSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    string? ReadWithFallback(string key)
    {
      var configured =
          (cfg != null && cfg.TryGetValue(key, out var cfgVal) ? cfgVal : null)
          ?? configuration?[$"Warm:{key}"]
          ?? configuration?[$"Cold:{key}"]
          ?? configuration?[key];

      return !string.IsNullOrWhiteSpace(configured)
          ? configured.Trim()
          : Environment.GetEnvironmentVariable(key)?.Trim();
    }

    backendOptions.Hosts.Clear();

    var hostsFileContent = new StringBuilder();

    foreach (var entry in ReadHostEntries(ReadWithFallback))
    {
        _logger?.LogInformation("Found a Host: {HostKey}, HostName: {Hostname}", entry.HostKey, entry.Hostname);

        try
        {
            var hostConfig = new HostConfig(entry.Hostname, entry.ProbePath, entry.Ip, backendOptions.OAuthAudience);
            backendOptions.Hosts.Add(hostConfig);
            hostCollection?.StageHost(hostConfig);
            hostsFileContent.AppendLine($"{entry.Ip} {hostConfig.Host}");
        }
        catch (UriFormatException e)
        {
            _logger?.LogError(e, "Could not add {HostKey} with {Hostname}", entry.HostKey, entry.Hostname);
        }
    }

    AppendHostsFileIfEnabled(
        ReadWithFallback("APPENDHOSTSFILE") ?? ReadWithFallback("AppendHostsFile"),
        hostsFileContent);

    // int i = 1;
    // StringBuilder sb = new();
    // while (true)
    // {

    //   var hostKey = $"Host{i}";
    //   var probePathKey = $"Probe_path{i}";
    //   var ipKey = $"IP{i}";


    //   var hostname = ReadWithFallback(hostKey);
    //   if (string.IsNullOrEmpty(hostname)) break;

    //   var probePath = ReadWithFallback(probePathKey);
    //   var ip = ReadWithFallback(ipKey);

    //   _logger.LogInformation($"Found a Host: {hostKey}, Probe Path: {probePathKey}, HostName: {hostname}");
    //   hostSettingsSnapshot[hostKey] = hostname;
    //   if (!string.IsNullOrEmpty(probePath))
    //   {
    //     hostSettingsSnapshot[probePathKey] = probePath;
    //   }

    //   if (!string.IsNullOrEmpty(ip))
    //   {
    //     hostSettingsSnapshot[ipKey] = ip;
    //   }

    //   try
    //   {
    //     _logger?.LogDebug($"Found host {hostname} with probe path {probePath} and IP {ip}");

    //     // Resolve HostConfig from DI using the factory
    //     HostConfig bh = new HostConfig(hostname, probePath, ip, backendOptions.OAuthAudience);
    //     backendOptions.Hosts.Add(bh);
    //     hostCollection?.StageHost(bh);

    //     sb.AppendLine($"{ip} {bh.Host}");
    //   }

    //   catch (UriFormatException e)
    //   {
    //     _logger?.LogError($"Could not add Host{i} with {hostname} : {e.Message}");
    //     Console.WriteLine(e.StackTrace);
    //   }

    //   i++;
    // }

    // var appendHostsFile = ReadWithFallback("APPENDHOSTSFILE")
    //   ?? ReadWithFallback("AppendHostsFile");

    // if (!string.IsNullOrEmpty(appendHostsFile))
    // {
    //   hostSettingsSnapshot["APPENDHOSTSFILE"] = appendHostsFile;
    // }

    // if (appendHostsFile?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
    // {
    //   _logger?.LogInformation($"Appending {sb} to /etc/hosts");
    //   using StreamWriter sw = File.AppendText("/etc/hosts");
    //   sw.WriteLine(sb.ToString());
    // }

    // Snapshot is updated only after all Host<n>/Probe_path<n>/IP<n> entries are parsed and applied.
    hostCollection?.Activate();
  }


  private record ParsedHostEntry(string HostKey, string Hostname, string? ProbePath, string? Ip);
  private static IEnumerable<ParsedHostEntry> ReadHostEntries(Func<string, string?> readWithFallback)
  {
    for (int i = 1; ; i++)
    {
      var hostname = readWithFallback($"Host{i}");
      if (string.IsNullOrEmpty(hostname)) yield break;

      yield return new ParsedHostEntry(
          $"Host{i}",
          hostname,
          readWithFallback($"Probe_path{i}"),
          readWithFallback($"IP{i}")
      );
    }
  }
  private static void AppendHostsFileIfEnabled(string? flag, StringBuilder hostsContent)
  {
    if (flag?.Equals("true", StringComparison.OrdinalIgnoreCase) != true) return;

    _logger?.LogInformation("Appending {HostEntries} to /etc/hosts", hostsContent);
    using StreamWriter sw = File.AppendText("/etc/hosts");
    sw.WriteLine(hostsContent.ToString());
  }
}
