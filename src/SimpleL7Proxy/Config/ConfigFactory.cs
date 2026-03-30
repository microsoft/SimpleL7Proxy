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
public static class ConfigFactory
{
  private static ILogger? _logger;

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
  /// Builds and returns two BackendOptions instances:
  /// <list type="bullet">
  ///   <item><c>baseOptions</c> — env vars only (pristine defaults snapshot, never mutated after startup).</item>
  ///   <item><c>envOptions</c> — env vars + App Config warm/cold overrides merged on top (warm wins on collision). This is the live singleton.</item>
  /// </list>
  /// </summary>
  public static async Task<(ProxyConfig baseOptions, ProxyConfig envOptions)> CreateOptions(AppConfigService appConfigBootstrap)
  {
    var baseOptions = ConfigParser.ApplyEnv(EffectiveEnvironment(), new ProxyConfig());

    var (warmSettings, coldSettings) = await appConfigBootstrap.GetSettingsAsync().ConfigureAwait(false);

    ProxyConfig envOptions;

    if (warmSettings == null && coldSettings == null) {
      envOptions = baseOptions.DeepClone();
    }
    else
    {
      var merged = new Dictionary<string, string>(coldSettings ?? new Dictionary<string, string>());
      if ( warmSettings != null)
        foreach (var kvp in warmSettings) merged[kvp.Key] = kvp.Value;

      envOptions = ConfigParser.ApplyEnv(merged, baseOptions);      
    }

    ConfigParser.ConfigureHttpClient(envOptions);

    return (baseOptions, envOptions);
  }

  /// <summary>
  /// Applies a warm-refresh: detects changes, updates the live options, re-registers
  /// backends, derives dependent settings, and notifies subscribers.
  /// Called by AppConfigBootstrap when the sentinel value changes.
  /// </summary>
  public static async Task ApplyRefresh(
      ProxyConfig liveOptions,
      ProxyConfig defaultOptions,
      Dictionary<string, string> warm,
      ConfigChangeNotifier? notifier,
      IHostHealthCollection? hostCollection,
      ILogger logger,
      CancellationToken ct)
  {
    var (changes, parsedValues, hostChanges) = DetectWarmChanges(liveOptions, warm, logger);
    if (changes.Count == 0 && hostChanges.Count == 0)
        return;

    var fields = ConfigMetadata.GetFieldsByConfigName();
    var ds = new Dictionary<string, string>(1);

    foreach (var kvp in parsedValues)
    {
        var field = fields[kvp.Key];
        var before = field.GetValue(liveOptions); // TODO: remove debug
        ds.Clear();
        ds[kvp.Key] = kvp.Value?.ToString() ?? "";
        liveOptions.ApplyFieldFromEnv(ds, defaultOptions, kvp.Key, field.Name);
        var after = field.GetValue(liveOptions); // TODO: remove debug
        Console.WriteLine($"[WARM] Applied {kvp.Key} ({field.Name}): '{before}' -> '{after}'"); // TODO: remove debug
    }

    // Collect changed PropertyInfos for derived-settings recalculation.
    var changedProps = new List<System.Reflection.PropertyInfo>(changes.Count);
    foreach (var change in changes)
    {
        if (fields.TryGetValue(change.PropertyName, out var prop))
            changedProps.Add(prop);
    }
    Console.WriteLine($"[WARM] {changedProps.Count} derived-settings prop(s) to recalculate"); // TODO: remove debug
    if (changedProps.Count > 0)
        ConfigParser.ApplyDerivedSettings(liveOptions, [.. changedProps]);

    if (hostChanges.Count > 0)
    {
        Console.WriteLine($"[WARM] {hostChanges.Count} host change(s) detected, re-registering backends"); // TODO: remove debug
        RegisterBackends(liveOptions, null, hostChanges, hostCollection);
    }

    if (changes.Count > 0)
    {
        Console.WriteLine($"[WARM] Notifying {changes.Count} change(s): {string.Join(", ", changes.Select(c => c.PropertyName))}"); // TODO: remove debug
        logger.LogInformation("[BOOTSTRAP] Applied {Count} warm change(s): {Names}",
            changes.Count, string.Join(", ", changes.Select(c => c.PropertyName)));
        if (notifier != null)
            await notifier.NotifyAsync(changes, liveOptions, ct);
    }
  }

  /// <summary>
  /// Diffs bare-keyed warm settings against live options. Does not mutate liveOptions.
  /// Host keys (Host*, Probe*, IP*) are returned separately in HostChanges.
  /// </summary>
  private static (List<ConfigChange> Changes, Dictionary<string, object?> ParsedValues, Dictionary<string, string> HostChanges) DetectWarmChanges(
      ProxyConfig liveOptions,
      Dictionary<string, string> warmSettings,
      ILogger? logger = null)
  {
    var changeList = new List<ConfigChange>();
    var updates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    var hostChanges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var defaultTarget = new ProxyConfig();
    var env = new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase);

    foreach (var kvp in warmSettings)
    {
        var rawValue = kvp.Value;
        if (string.IsNullOrEmpty(rawValue)) continue;

        var key = kvp.Key;
        if (key.StartsWith("Host") || key.StartsWith("Probe") || key.StartsWith("IP"))
        {
            hostChanges[key] = rawValue;
            continue;
        }

        if (!ConfigMetadata.WarmDescriptorsByKeyPath.TryGetValue(key, out var descriptor)
            && !ConfigMetadata.WarmDescriptorsByConfigName.TryGetValue(key, out descriptor))
            continue;

        var configName = descriptor.ConfigName;
        if (!ConfigMetadata.TryGetFieldByConfigName(configName, out var field) || field == null)
            continue;

        var currentValue = field.GetValue(liveOptions);

        // Parse the raw value via ApplyFieldFromEnv on a throwaway target.
        env.Clear();
        env[configName] = rawValue;

        defaultTarget.ApplyFieldFromEnv(env, liveOptions, configName, field.Name);
        var newValue = field.GetValue(defaultTarget);

        if (DeepEquals(currentValue, newValue)) continue;

        updates[configName] = newValue;
        changeList.Add(new ConfigChange
        {
            PropertyName = configName,
            KeyPath = descriptor.Attribute.KeyPath,
            RawOldValue = currentValue,
            RawNewValue = newValue
        });
    }

    return (changeList, updates, hostChanges);
  }

  /// <summary>Deep-compares two values, handling List, array, and Dictionary types.</summary>
  private static bool DeepEquals(object? a, object? b)
  {
    if (ReferenceEquals(a, b)) return true;
    if (a is null || b is null) return false;
    if (a.GetType() != b.GetType()) return false;

    return (a, b) switch
    {
        (int[] la, int[] lb) => la.SequenceEqual(lb),
        (IList<string> la, IList<string> lb) => la.SequenceEqual(lb),
        (IList<int> la, IList<int> lb) => la.SequenceEqual(lb),
        (IDictionary<string, string> da, IDictionary<string, string> db) =>
            da.Count == db.Count && da.All(kvp => db.TryGetValue(kvp.Key, out var v) && v == kvp.Value),
        (IDictionary<int, int> da, IDictionary<int, int> db) =>
            da.Count == db.Count && da.All(kvp => db.TryGetValue(kvp.Key, out var v) && v == kvp.Value),
        _ => Equals(a, b)
    };
  }

  /// <summary>
  /// Emits a telemetry event with all resolved config values,
  /// masking sensitive keys (connection strings, secrets, etc.).
  /// </summary>
  public static void OutputEnvVars(ProxyConfig backendOptions)
  {

    ProxyEvent pe = new()
    {
      Type = EventType.CustomEvent,
      ["Message"] = "Configuration loaded",
    };

    var warm = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var cold = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var hidden = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var prop in typeof(ProxyConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
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
  public static IServiceCollection RegisterBackendOptions(this IServiceCollection services, ILogger logger, ProxyConfig backendOptions)
  {
    _logger = logger;

    var wrapper = new OptionsWrapper<ProxyConfig>(backendOptions);
    services.AddSingleton(backendOptions);
    services.AddSingleton<IOptions<ProxyConfig>>(wrapper);
    services.AddSingleton<IOptionsMonitor<ProxyConfig>>(new SingletonOptionsMonitor<ProxyConfig>(backendOptions));

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
    if (envValue?.Trim() == ConfigMetadata.DefaultPlaceholder) envValue = null;
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
    if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigMetadata.DefaultPlaceholder)
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
    ProxyConfig backendOptions, 
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
