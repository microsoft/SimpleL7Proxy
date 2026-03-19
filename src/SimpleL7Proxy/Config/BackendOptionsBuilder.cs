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
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Builds and registers the singleton BackendOptions instance.
/// Handles environment variable collection, App Config merging,
/// backend host discovery, and DI registration.
/// </summary>
public static class BackendOptionsBuilder
{
  private static ILogger? _logger;

  static BackendOptions s_options = new BackendOptions();

  /// <summary>
  /// Collects all OS env vars and seeds the Hostname identity fallback chain:
  /// explicit Hostname > ReplicaID > CONTAINER_APP_REPLICA_NAME > HOSTNAME > MachineName.
  /// </summary>
  public static Dictionary<string, string> EffectiveEnvironment()
  {
    var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
    {
      if (de.Key?.ToString() is string key)
        env[key] = de.Value?.ToString() ?? string.Empty;
    }

    if (!env.ContainsKey("Hostname"))
    {
      env["Hostname"] =
          env.GetValueOrDefault("ReplicaID")
          ?? Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
          ?? Environment.GetEnvironmentVariable("HOSTNAME")
          ?? Environment.MachineName;
    }

    return env;
  }

  /// <summary>
  /// Builds the initial BackendOptions: env vars first, then App Config warm+cold
  /// overrides merged on top (warm wins on collision).
  /// </summary>
  public static async Task<BackendOptions> CreateOptions(AppConfigBootstrap appConfigBootstrap)
  {
    s_options.Apply(EffectiveEnvironment());

    var (warmSettings, coldSettings) = await appConfigBootstrap.GetSettingsAsync().ConfigureAwait(false);

    BackendOptions envOptions;

    if (warmSettings == null && coldSettings == null) {
      envOptions = s_options;
    }
    else
    {
      var merged = new Dictionary<string, string>(coldSettings ?? new Dictionary<string, string>());
      if ( warmSettings != null)
        foreach (var kvp in warmSettings) merged[kvp.Key] = kvp.Value;

      envOptions = ConfigParser.ApplyEnv(merged, s_options);      
    }

    envOptions.ConfigureHttpClient();

    return envOptions;
  }

  /// <summary>
  /// Emits a telemetry event with all resolved config values,
  /// masking sensitive keys (connection strings, secrets, etc.).
  /// </summary>
  public static void OutputEnvVars(BackendOptions backendOptions)
  {

    ProxyEvent pe = new()
    {
      Type = EventType.CustomEvent,
      ["Message"] = "Configuration loaded",
    };

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
      pe[attr.KeyPath]= display;  
    }

    pe.SendEvent();

    var all = warm.Concat(cold).Concat(hidden).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    string json = System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

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

  /// <summary>
  /// Registers BackendOptions as a singleton for IOptions, IOptionsMonitor, and direct injection.
  /// All resolve to the same instance so in-place warm-refresh is visible everywhere.
  /// </summary>
  public static IServiceCollection RegisterBackendOptions(this IServiceCollection services, ILogger logger, BackendOptions backendOptions)
  {
    _logger = logger;

    var wrapper = new OptionsWrapper<BackendOptions>(backendOptions);
    services.AddSingleton(backendOptions);
    services.AddSingleton<IOptions<BackendOptions>>(wrapper);
    services.AddSingleton<IOptionsMonitor<BackendOptions>>(new SingletonOptionsMonitor<BackendOptions>(backendOptions));

    return services;
  }

  /// <summary>IOptionsMonitor that always returns the same singleton. No change notifications.</summary>
  private sealed class SingletonOptionsMonitor<T>(T instance) : IOptionsMonitor<T>
  {
    public T CurrentValue => instance;
    public T Get(string? name) => instance;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  private static int ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
  {
    int value = _ReadEnvironmentVariableOrDefault(env, variableName, defaultValue);
    return value;
  }

  private static bool ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, bool defaultValue)
  {
    bool value = _ReadEnvironmentVariableOrDefault(env, variableName, defaultValue);
    return value;
  }

  private static readonly System.Data.DataTable s_mathTable = new();

  /// <summary>Tries to evaluate a simple arithmetic expression (e.g. "60*10").</summary>
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

  /// <summary>Reads an int from the env dict, falling back to math expression eval then default.</summary>
  private static int _ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
  {
    var envValue = env.GetValueOrDefault(variableName);
    if (envValue?.Trim() == ConfigOptions.DefaultPlaceholder) envValue = null;
    if (!int.TryParse(envValue, out var value))
    {
      if (TryEvaluateMathExpression(envValue!, out var mathResult))
        return (int)mathResult;
      return defaultValue;
    }
    return value;
  }

  /// <summary>Reads a bool from the env dict, falling back to default.</summary>
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

  /// <summary>
  /// Rebuilds the backend host list from Host1..N, Probe_path1..N, IP1..N keys.
  /// Resolution priority: appConfigSettings → fallbackConfig (Warm/Cold/bare) → env var.
  /// Optionally appends to /etc/hosts for Linux container deployments.
  /// </summary>
  public static void RegisterBackends(
    BackendOptions backendOptions, 
    IConfiguration? fallbackConfig = null, 
    Dictionary<string, string>? appConfigSettings = null, 
    IHostHealthCollection? hostCollection = null)
  {
    // Resolve a key using cascading priority:
    //   1. appConfigSettings dict (e.g. warm snapshot)
    //   2. IConfiguration Warm: prefix
    //   3. IConfiguration Cold: prefix
    //   4. IConfiguration bare key
    //   5. OS environment variable
    string? ReadWithFallback(string key)
    {
      var configured =
          (appConfigSettings != null && appConfigSettings.TryGetValue(key, out var cfgVal) ? cfgVal : null)
          ?? fallbackConfig?[$"Warm:{key}"]
          ?? fallbackConfig?[$"Cold:{key}"]
          ?? fallbackConfig?[key];

      return !string.IsNullOrWhiteSpace(configured)
          ? configured.Trim()
          : Environment.GetEnvironmentVariable(key)?.Trim();
    }

    var hostsFileContent = new StringBuilder();

    foreach (var entry in ReadHostEntries(ReadWithFallback))
    {
        try
        {
            var hostConfig = new HostConfig(entry.Hostname, entry.ProbePath, entry.Ip, backendOptions.OAuthAudience);
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

    hostCollection?.Activate();
  }

  private record ParsedHostEntry(string HostKey, string Hostname, string? ProbePath, string? Ip);

  /// <summary>Yields Host1..N entries until a gap is found.</summary>
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

  /// <summary>Appends host/IP pairs to /etc/hosts when APPENDHOSTSFILE=true.</summary>
  private static void AppendHostsFileIfEnabled(string? flag, StringBuilder hostsContent)
  {
    if (flag?.Equals("true", StringComparison.OrdinalIgnoreCase) != true) return;

    _logger?.LogInformation("Appending {HostEntries} to /etc/hosts", hostsContent);
    using StreamWriter sw = File.AppendText("/etc/hosts");
    sw.WriteLine(hostsContent.ToString());
  }
}
