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
  private static readonly BackendOptions s_defaults = new();


  public static BackendOptions CreateBackendOptions(ILogger logger)
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
    var appConfigSettings = AppConfigBootstrap.WaitForDownload();
    if (appConfigSettings != null)
    {
      foreach (var kvp in appConfigSettings)
      {
        // strip  ["  and  "] from keys and values if present to support both raw and JSON-style formats
        string key = kvp.Key.Trim().TrimStart('[').TrimEnd(']').TrimStart('"').TrimEnd('"');
        string value = kvp.Value.Trim().TrimStart('[').TrimEnd(']').TrimStart('"').TrimEnd('"');
        effectiveEnvironment[key] = value;
      }
      _logger?.LogInformation("[BOOTSTRAP] Applied {Count} App Configuration value(s) to effective environment", appConfigSettings.Count);
    }

    var backendOptions = ConfigParser.ParseOptions(effectiveEnvironment, logger);
    ConfigureHttpClientFromOptions(effectiveEnvironment, backendOptions);

    OutputEnvVars();

    return backendOptions;
  }

    private static void OutputEnvVars()
  {
    static string MaskValue(string key, string value)
    {
      if (string.IsNullOrEmpty(value))
        return value;

      var lower = key.ToLowerInvariant();
      var isSensitive = lower.Contains("connectionstring") ||
                        lower.Contains("password") ||
                        lower.Contains("secret") ||
                        lower.Contains("token") ||
                        lower.Contains("apikey") ||
                        lower.Contains("sas");

      if (!isSensitive)
        return value;

      if (value.Length <= 4)
        return "****";

      return $"{value.Substring(0, 2)}***{value.Substring(value.Length - 2)}";
    }

    const int keyWidth = 27;
    const int valWidth = 30;
    const int gutterWidth = 4;
    int col = 0;
    string? pendingEntry = null;
    foreach (var kvp in ConfigParser.GetParsedEnvVars())
    {
      string key = kvp.Key;
      string value = MaskValue(key, kvp.Value);

      string entry = $"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}:" +
                  $"{(value.Length > valWidth ? value.Substring(0, valWidth - 3) + "..." : value),-valWidth}";

      if (col == 0)
      {
        pendingEntry = entry;
        col = 1;
      }
      else
      {
        if (key.Length > keyWidth || value.Length > valWidth)
        {
          Console.WriteLine(pendingEntry);
          Console.WriteLine($"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}: {value}");
          pendingEntry = null;
          col = 0;
        }
        else
        {
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

}
