using Microsoft.Extensions.Configuration;
using SimpleL7Proxy.Backend.Iterators;
using System.Reflection;

namespace SimpleL7Proxy.Config;

public static class ConfigParser
{
    private static readonly Dictionary<string, string> EnvVars = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BackendOptions s_defaults = new();
    private static readonly System.Data.DataTable s_mathTable = new();

    private static readonly (string envVar, string property)[] SimpleFields =
    new (string envVar, string property)[]
    {
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

        ("SuccessRate", "SuccessRate"),
        ("UserPriorityThreshold", "UserPriorityThreshold"),

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
    };

    public static BackendOptions ParseOptions(Dictionary<string, string> env)
    {
        EnvVars.Clear();

        var opts = new BackendOptions();
        var defaults = s_defaults;

        foreach (var (envVar, property) in SimpleFields)
        {
            ApplyFieldFromEnv(env, opts, defaults, envVar, property);
        }

        opts.AcceptableStatusCodes = ReadEnvironmentVariableOrDefault(env, "AcceptableStatusCodes", defaults.AcceptableStatusCodes);
        opts.IterationMode = ReadEnvironmentVariableOrDefault(env, "IterationMode", defaults.IterationMode);

        var defaultPriorityWorkers = string.Join(",", defaults.PriorityWorkers.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        opts.PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault(env, "PriorityWorkers", defaultPriorityWorkers)));

        var defaultValidateHeaders = string.Join(",", defaults.ValidateHeaders.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        opts.ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault(env, "ValidateHeaders", defaultValidateHeaders)));

        ApplyAsyncServiceBusOverrides(env, opts, defaults);
        ApplyAsyncBlobStorageOverrides(env, opts, defaults);

        var replicaId = ReadEnvironmentVariableOrDefault(
            env,
            "ReplicaID",
            Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName);
        ApplyReplicaIdentitySettings(env, opts, replicaId);

        ApplyDerivedSettingsFromConfigNames(
            opts,
            configuration: null,
            nameof(BackendOptions.HealthProbeSidecar),
            nameof(BackendOptions.LoadBalanceMode),
            nameof(BackendOptions.PriorityKeys),
            nameof(BackendOptions.PriorityValues),
            nameof(BackendOptions.ValidateHeaders));

        return opts;
    }

    public static IReadOnlyDictionary<string, string> GetParsedEnvVars()
    {
        return new Dictionary<string, string>(EnvVars, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies a single configuration field from the environment dictionary to the target <see cref="BackendOptions"/> instance.
    /// Uses reflection to set the named property, falling back to the corresponding default value when the
    /// environment variable is absent or set to the default placeholder. Supports int, double, float, string,
    /// bool, List&lt;string&gt;, List&lt;int&gt;, int[], Dictionary&lt;string, string&gt;, and enum property types.
    /// </summary>
    /// <param name="env">Dictionary of environment/configuration key-value pairs to read from.</param>
    /// <param name="target">The <see cref="BackendOptions"/> instance whose property will be set.</param>
    /// <param name="defaults">A default <see cref="BackendOptions"/> instance providing fallback values.</param>
    /// <param name="envVar">The environment variable (dictionary key) to look up.</param>
    /// <param name="property">The name of the <see cref="BackendOptions"/> property to set.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="property"/> does not exist on <see cref="BackendOptions"/>.</exception>
    /// <exception cref="NotSupportedException">Thrown when the property type is not handled.</exception>
    public static void ApplyFieldFromEnv(Dictionary<string, string> env, BackendOptions target, BackendOptions defaults, string envVar, string property)
    {
        var pi = typeof(BackendOptions).GetProperty(property) ?? throw new InvalidOperationException($"Unknown BackendOptions property: {property}");
        var defVal = pi.GetValue(defaults);
        var type = pi.PropertyType;

        if (type == typeof(int) || type == typeof(double))
        {
            var val = ReadEnvironmentVariableOrDefault(env, envVar, Convert.ToInt32(defVal));
            pi.SetValue(target, Convert.ChangeType(val, type));
        }
        else if (type == typeof(float))
        {
            var val = ReadEnvironmentVariableOrDefault(env, envVar, Convert.ToSingle(defVal));
            pi.SetValue(target, Convert.ChangeType(val, type));
        }
        else if (type == typeof(string))
        {
            pi.SetValue(target, ReadEnvironmentVariableOrDefault(env, envVar, (string)defVal!));
        }
        else if (type == typeof(bool))
        {
            pi.SetValue(target, ReadEnvironmentVariableOrDefault(env, envVar, (bool)defVal!));
        }
        else if (type == typeof(List<string>))
        {
            var value = ReadEnvironmentVariableOrDefault(env, envVar, string.Join(",", (List<string>)defVal!));
            pi.SetValue(target, ToListOfString(value));
        }
        else if (type == typeof(List<int>))
        {
            var value = ReadEnvironmentVariableOrDefault(env, envVar, string.Join(",", (List<int>)defVal!));
            pi.SetValue(target, ToListOfInt(value));
        }
        else if (type == typeof(int[]))
        {
            pi.SetValue(target, ReadEnvironmentVariableOrDefault(env, envVar, (int[])defVal!));
        }
        else if (type == typeof(Dictionary<string, string>))
        {
            var defaultValue = string.Join(",", ((Dictionary<string, string>)defVal!).Select(kvp => $"{kvp.Key}={kvp.Value}"));
            var value = ReadEnvironmentVariableOrDefault(env, envVar, defaultValue);
            pi.SetValue(target, KVStringPairs(ToListOfString(value)));
        }
        else if (type.IsEnum)
        {
            var value = ReadEnvironmentVariableOrDefault(env, envVar, defVal!.ToString()!);
            if (Enum.TryParse(type, value, true, out var parsed))
            {
                pi.SetValue(target, parsed);
            }
            else
            {
                pi.SetValue(target, defVal);
            }
        }
        else
        {
            throw new NotSupportedException($"ApplyFieldFromEnv: unsupported property type {type.Name} for {property}");
        }
    }

    public static void ApplyDerivedSettings(BackendOptions backendOptions, params PropertyInfo[] changedProperties)
    {
        if (changedProperties.Length == 0)
        {
            return;
        }

        var changedPropertyNames = new HashSet<string>(
            changedProperties.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        if (changedPropertyNames.Contains(nameof(BackendOptions.HealthProbeSidecar)))
        {
            ParseHealthProbeSidecarSettings(backendOptions);
        }

        if (changedPropertyNames.Contains(nameof(BackendOptions.LoadBalanceMode)))
        {
            ValidateLoadBalanceMode(backendOptions, s_defaults);
        }

        if (changedPropertyNames.Contains(nameof(BackendOptions.PriorityKeys))
            || changedPropertyNames.Contains(nameof(BackendOptions.PriorityValues)))
        {
            ValidatePrioritySettings(backendOptions, s_defaults);
        }

        if (changedPropertyNames.Contains(nameof(BackendOptions.ValidateHeaders)))
        {
            ValidateHeaderSettings(backendOptions);
        }
    }

    public static void ApplyDerivedSettingsFromConfigNames(
        BackendOptions backendOptions,
        IConfiguration? configuration,
        params string[] changedConfigNames)
    {
        if (changedConfigNames.Length == 0)
        {
            return;
        }

        var descriptorByConfigName = ConfigOptions.GetDescriptors()
            .ToDictionary(d => d.ConfigName, d => d.Property, StringComparer.OrdinalIgnoreCase);

        var changedProperties = new List<PropertyInfo>(changedConfigNames.Length);
        var shouldRefreshBackends = false;

        foreach (var changedConfigName in changedConfigNames)
        {
            if (string.IsNullOrWhiteSpace(changedConfigName))
            {
                continue;
            }

            if (descriptorByConfigName.TryGetValue(changedConfigName, out var property))
            {
                changedProperties.Add(property);
            }

            if (IsBackendHostConfigName(changedConfigName))
            {
                shouldRefreshBackends = true;
            }
        }

        if (changedProperties.Count > 0)
        {
            ApplyDerivedSettings(backendOptions, [.. changedProperties]);
        }

        if (shouldRefreshBackends)
        {
            ConfigBootstrapper.RegisterBackends(backendOptions, configuration, null);
        }
    }

    public static bool IsBackendHostConfigName(string configName)
    {
        if (string.IsNullOrWhiteSpace(configName))
        {
            return false;
        }

        var normalized = configName;
        if (normalized.StartsWith("Warm:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Warm:".Length..];
        }
        else if (normalized.StartsWith("Cold:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["Cold:".Length..];
        }

        static bool IsIndexedKey(string value, string prefix)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffix = value[prefix.Length..];
            return int.TryParse(suffix, out _);
        }

        return IsIndexedKey(normalized, "Host")
            || IsIndexedKey(normalized, "IP")
            || IsIndexedKey(normalized, "Probe_path")
            || normalized.Equals("APPENDHOSTSFILE", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("AppendHostsFile", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyAsyncServiceBusOverrides(Dictionary<string, string> env, BackendOptions opts, BackendOptions defaults)
    {
        var configStr = ReadEnvironmentVariableOrDefault(env, "AsyncSBConfig", defaults.AsyncSBConfig);
        var (connStr, ns, queue, useMi) = ParseServiceBusConfig(configStr);
        opts.AsyncSBConfig = configStr;
        opts.AsyncSBConnectionString = ReadEnvironmentVariableOrDefault(env, "AsyncSBConnectionString", connStr);
        opts.AsyncSBNamespace = ReadEnvironmentVariableOrDefault(env, "AsyncSBNamespace", ns);
        opts.AsyncSBQueue = ReadEnvironmentVariableOrDefault(env, "AsyncSBQueue", queue);
        opts.AsyncSBUseMI = ReadEnvironmentVariableOrDefault(env, "AsyncSBUseMI", useMi);
    }

    private static void ApplyAsyncBlobStorageOverrides(Dictionary<string, string> env, BackendOptions opts, BackendOptions defaults)
    {
        var configStr = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageConfig", defaults.AsyncBlobStorageConfig);
        var (connStr, accountUri, useMi) = ParseBlobStorageConfig(configStr);
        opts.AsyncBlobStorageConfig = configStr;
        opts.AsyncBlobStorageAccountUri = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageAccountUri", accountUri);
        opts.AsyncBlobStorageConnectionString = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageConnectionString", connStr);
        opts.AsyncBlobStorageUseMI = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageUseMI", useMi);
    }

    private static void ApplyReplicaIdentitySettings(Dictionary<string, string> env, BackendOptions opts, string replicaId)
    {
        opts.HostName = ReadEnvironmentVariableOrDefault(env, "Hostname", replicaId);
        opts.IDStr = $"{ReadEnvironmentVariableOrDefault(env, "RequestIDPrefix", "S7P")}-{replicaId}-";
    }

    private static void ParseHealthProbeSidecarSettings(BackendOptions backendOptions)
    {
        var healthSettings = backendOptions.HealthProbeSidecar.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var setting in healthSettings)
        {
            var kvp = setting.Split('=', 2);
            if (kvp.Length != 2) continue;

            var key = kvp[0].Trim().ToLowerInvariant();
            var value = kvp[1].Trim().ToLowerInvariant();
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

    private static void ValidatePrioritySettings(BackendOptions backendOptions, BackendOptions defaults)
    {
        if (backendOptions.PriorityKeys.Count != backendOptions.PriorityValues.Count)
        {
            backendOptions.PriorityValues = Enumerable.Repeat(defaults.DefaultPriority, backendOptions.PriorityKeys.Count).ToList();
        }

        int workerAllocation = 0;
        foreach (var key in backendOptions.PriorityWorkers.Keys)
        {
            workerAllocation += backendOptions.PriorityWorkers[key];
        }

        if (workerAllocation > backendOptions.Workers)
        {
            backendOptions.Workers = workerAllocation;
        }
    }

    private static void ValidateHeaderSettings(BackendOptions backendOptions)
    {
        if (backendOptions.ValidateHeaders.Count > 0)
        {
            foreach (var (key, value) in backendOptions.ValidateHeaders)
            {
                if (!backendOptions.RequiredHeaders.Contains(key))
                {
                    backendOptions.RequiredHeaders.Add(key);
                }
                if (!backendOptions.RequiredHeaders.Contains(value))
                {
                    backendOptions.RequiredHeaders.Add(value);
                }
                if (!backendOptions.DisallowedHeaders.Contains(value))
                {
                    backendOptions.DisallowedHeaders.Add(value);
                }
            }
        }
    }

    private static void ValidateLoadBalanceMode(BackendOptions backendOptions, BackendOptions defaults)
    {
        backendOptions.LoadBalanceMode = backendOptions.LoadBalanceMode.Trim().ToLowerInvariant();
        if (backendOptions.LoadBalanceMode != Constants.Latency &&
            backendOptions.LoadBalanceMode != Constants.RoundRobin &&
            backendOptions.LoadBalanceMode != Constants.Random)
        {
            backendOptions.LoadBalanceMode = defaults.LoadBalanceMode;
        }
    }

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
        catch
        {
            return false;
        }
    }

    private static int ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
    {
        int value = ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static int[] ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int[] defaultValues)
    {
        int[] value = ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValues);
        EnvVars[variableName] = string.Join(",", value);
        return value;
    }

    private static float ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, float defaultValue)
    {
        float value = ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static string ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, string defaultValue)
    {
        string value = ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
        EnvVars[variableName] = value;
        return value;
    }

    private static IterationModeEnum ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, IterationModeEnum defaultValue)
    {
        string? envValue = env.GetValueOrDefault(variableName)?.Trim();
        if (string.IsNullOrEmpty(envValue) || envValue == ConfigOptions.DefaultPlaceholder || !Enum.TryParse(envValue, true, out IterationModeEnum value))
        {
            EnvVars[variableName] = defaultValue.ToString();
            return defaultValue;
        }

        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static bool ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, bool defaultValue)
    {
        bool value = ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static int ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, int defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (envValue?.Trim() == ConfigOptions.DefaultPlaceholder) envValue = null;

        if (!int.TryParse(envValue, out var value))
        {
            if (TryEvaluateMathExpression(envValue ?? string.Empty, out var mathResult))
            {
                return (int)mathResult;
            }
            return defaultValue;
        }

        return value;
    }

    private static int[] ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, int[] defaultValues)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
        {
            return defaultValues;
        }

        try
        {
            var trimmed = envValue.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                trimmed = trimmed[1..^1];
            }

            return trimmed.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
        }
        catch
        {
            return defaultValues;
        }
    }

    private static float ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, float defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (envValue?.Trim() == ConfigOptions.DefaultPlaceholder) envValue = null;

        if (!float.TryParse(envValue, out var value))
        {
            if (TryEvaluateMathExpression(envValue ?? string.Empty, out var mathResult))
            {
                return (float)mathResult;
            }
            return defaultValue;
        }

        return value;
    }

    private static string ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, string defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
        {
            return defaultValue;
        }

        return envValue.Trim();
    }

    private static bool ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, bool defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigOptions.DefaultPlaceholder)
        {
            return defaultValue;
        }

        return envValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<int, int> KVIntPairs(List<string> list)
    {
        Dictionary<int, int> keyValuePairs = [];

        foreach (var item in list)
        {
            var kvp = item.Split(':');
            if (kvp.Length == 2 && int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value))
            {
                keyValuePairs[key] = value;
            }
        }

        return keyValuePairs;
    }

    private static Dictionary<string, string> KVStringPairs(List<string> list, char delimiter = '=')
    {
        char fallback = delimiter == '=' ? ':' : '=';
        Dictionary<string, string> keyValuePairs = [];

        foreach (var item in list)
        {
            var kvp = item.Split(delimiter, 2);
            if (kvp.Length == 2)
            {
                keyValuePairs[kvp[0].Trim()] = kvp[1].Trim();
                continue;
            }

            kvp = item.Split(fallback, 2);
            if (kvp.Length == 2)
            {
                keyValuePairs[kvp[0].Trim()] = kvp[1].Trim();
            }
        }

        return keyValuePairs;
    }

    private static List<string> ToListOfString(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return [];
        }

        var trimmed = s.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return [.. trimmed.Split(',').Select(p => p.Trim())];
    }

    private static List<int> ToListOfInt(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return [];
        }

        var trimmed = s.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Split(',').Select(p => int.Parse(p.Trim())).ToList();
    }

    private static Dictionary<string, string> ParseConfigString(string config, Dictionary<string, string[]> keyAliases)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(config))
        {
            return result;
        }

        var parts = config.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length == 0 || !parts[0].Contains('='))
        {
            return result;
        }

        foreach (var part in parts)
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length != 2)
            {
                continue;
            }

            var key = kvp[0].Trim().ToLowerInvariant();
            var value = kvp[1].Trim();

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
        }

        return result;
    }

    private static bool IsNamedKeyValueFormat(string[] parts, Dictionary<string, string[]> keyAliases)
    {
        if (parts.Length == 0)
        {
            return false;
        }

        var kvp = parts[0].Split('=', 2);
        if (kvp.Length != 2)
        {
            return false;
        }

        var firstKey = kvp[0].Trim();
        return keyAliases.Values.SelectMany(v => v).Any(alias => alias.Equals(firstKey, StringComparison.OrdinalIgnoreCase));
    }

    private static (string connectionString, string namespace_, string queue, bool useMI) ParseServiceBusConfig(string config)
    {
        string connectionString = "example-sb-connection-string";
        string namespace_ = "";
        string queue = "requeststatus";
        bool useMI = false;

        if (string.IsNullOrEmpty(config))
        {
            return (connectionString, namespace_, queue, useMI);
        }

        var parts = config.Split(',').Select(p => p.Trim()).ToArray();
        var keyAliases = new Dictionary<string, string[]>
        {
            { "connectionString", ["connectionstring", "cs"] },
            { "namespace", ["namespace", "ns"] },
            { "queue", ["queue", "q"] },
            { "useMI", ["usemi", "mi"] }
        };

        if (IsNamedKeyValueFormat(parts, keyAliases))
        {
            var parsed = ParseConfigString(config, keyAliases);
            if (parsed.TryGetValue("connectionString", out var cs)) connectionString = cs;
            if (parsed.TryGetValue("namespace", out var ns)) namespace_ = ns;
            if (parsed.TryGetValue("queue", out var q)) queue = q;
            if (parsed.TryGetValue("useMI", out var mi)) useMI = mi.Equals("true", StringComparison.OrdinalIgnoreCase);
            return (connectionString, namespace_, queue, useMI);
        }

        if (parts.Length == 4)
        {
            useMI = parts[3].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            return (parts[0], parts[1], parts[2], useMI);
        }

        return (connectionString, namespace_, queue, useMI);
    }

    private static (string connectionString, string accountUri, bool useMI) ParseBlobStorageConfig(string config)
    {
        string connectionString = "";
        string accountUri = "https://mystorageaccount.blob.core.windows.net/";
        bool useMI = false;

        if (string.IsNullOrEmpty(config))
        {
            return (connectionString, accountUri, useMI);
        }

        var parts = config.Split(',').Select(p => p.Trim()).ToArray();
        var keyAliases = new Dictionary<string, string[]>
        {
            { "connectionString", ["connectionstring", "cs"] },
            { "accountUri", ["accounturi", "uri"] },
            { "useMI", ["usemi", "mi"] }
        };

        if (IsNamedKeyValueFormat(parts, keyAliases))
        {
            var parsed = ParseConfigString(config, keyAliases);
            if (parsed.TryGetValue("connectionString", out var cs)) connectionString = cs;
            if (parsed.TryGetValue("accountUri", out var uri)) accountUri = uri;
            if (parsed.TryGetValue("useMI", out var mi)) useMI = mi.Equals("true", StringComparison.OrdinalIgnoreCase);
            return (connectionString, accountUri, useMI);
        }

        if (parts.Length == 3)
        {
            useMI = parts[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            return (parts[0], parts[1], useMI);
        }

        return (connectionString, accountUri, useMI);
    }
}
