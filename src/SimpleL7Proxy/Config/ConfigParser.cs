using System.Net;
using System.Net.Sockets;
using SimpleL7Proxy.Backend.Iterators;
using SimpleL7Proxy.Events;
using System.Reflection;

namespace SimpleL7Proxy.Config;

public static class ConfigParser
{
    private static readonly ProxyConfig s_defaults = new();
    private static readonly System.Data.DataTable s_mathTable = new();

    private static readonly (string envVar, string property)[] SimpleFields =
    [
        ("AsyncBlobWorkerCount", "AsyncBlobWorkerCount"),
        ("AsyncTimeout", "AsyncTimeout"),
        ("AsyncTTLSecs", "AsyncTTLSecs"),
        ("AsyncTriggerTimeout", "AsyncTriggerTimeout"),
        ("CBErrorThreshold", "CircuitBreakerErrorThreshold"),
        ("CBTimeslice", "CircuitBreakerTimeslice"),
        ("DefaultPriority", "DefaultPriority"),
        ("DefaultTTLSecs", "DefaultTTLSecs"),
        ("MaxQueueLength", "MaxQueueLength"),
        ("MaxEvents", "MaxEvents"),
        ("MaxAttempts", "MaxAttempts"),
        ("PollInterval", "PollInterval"),
        ("PollTimeout", "PollTimeout"),
        ("Port", "Port"),
        ("TERMINATION_GRACE_PERIOD_SECONDS", "TerminationGracePeriodSeconds"),
        ("Timeout", "Timeout"),
        ("UserConfigRefreshIntervalSecs", "UserConfigRefreshIntervalSecs"),
        ("UserSoftDeleteTTLMinutes", "UserSoftDeleteTTLMinutes"),
        ("Workers", "Workers"),
        ("SharedIteratorTTLSeconds", "SharedIteratorTTLSeconds"),
        ("SharedIteratorCleanupIntervalSeconds", "SharedIteratorCleanupIntervalSeconds"),

        ("SuccessRate", "SuccessRate"),
        ("UserPriorityThreshold", "UserPriorityThreshold"),

        ("AsyncClientRequestHeader", "AsyncClientRequestHeader"),
        ("AsyncClientConfigFieldName", "AsyncClientConfigFieldName"),
        ("HealthProbeSidecar", "HealthProbeSidecar"),
        ("LoadBalanceMode", "LoadBalanceMode"),
        ("OAuthAudience", "OAuthAudience"),
        ("PriorityKeyHeader", "PriorityKeyHeader"),
        ("StorageDbContainerName", "StorageDbContainerName"),
        ("SuspendedUserConfigUrl", "SuspendedUserConfigUrl"),
        ("TimeoutHeader", "TimeoutHeader"),
        ("TTLHeader", "TTLHeader"),
        ("UserConfigUrl", "UserConfigUrl"),
        ("LookupHeaderName", "UserIDFieldName"),  // older field name, kept for backward compatibility
        ("UserIDFieldName", "UserIDFieldName"),   // newer field name
        ("UserProfileHeader", "UserProfileHeader"),
        ("ValidateAuthAppFieldName", "ValidateAuthAppFieldName"),
        ("ValidateAuthAppID", "ValidateAuthAppID"),
        ("ValidateAuthAppIDHeader", "ValidateAuthAppIDHeader"),
        ("ValidateAuthAppIDUrl", "ValidateAuthAppIDUrl"),

        ("AsyncModeEnabled", "AsyncModeEnabled"),
        ("LogAllRequestHeaders", "LogAllRequestHeaders"),
        ("LogAllResponseHeaders", "LogAllResponseHeaders"),
        ("StorageDbEnabled", "StorageDbEnabled"),
        ("UseOAuth", "UseOAuth"),
        ("UseOAuthGov", "UseOAuthGov"),
        ("UseProfiles", "UseProfiles"),
        ("UseSharedIterators", "UseSharedIterators"),
        ("UserConfigRequired", "UserConfigRequired"),

        ("DependancyHeaders", "DependancyHeaders"),
        ("DisallowedHeaders", "DisallowedHeaders"),
        ("LogAllRequestHeadersExcept", "LogAllRequestHeadersExcept"),
        ("LogAllResponseHeadersExcept", "LogAllResponseHeadersExcept"),
        ("LogHeaders", "LogHeaders"),
        ("LogToConsole", "LogToConsole"),
        ("LogToEvents", "LogToEvents"),
        ("LogToAI", "LogToAI"),
        ("PriorityKeys", "PriorityKeys"),
        ("PriorityValues", "PriorityValues"),
        ("RequiredHeaders", "RequiredHeaders"),
        ("StripRequestHeaders", "StripRequestHeaders"),
        ("StripResponseHeaders", "StripResponseHeaders"),
        ("UniqueUserHeaders", "UniqueUserHeaders"),

        // ── Logging / Telemetry ──
        ("LOG_LEVEL", "LogLevel"),
        ("APPINSIGHTS_CONNECTIONSTRING", "AppInsightsConnectionString"),
        ("EVENT_LOGGERS", "EventLoggers"),
        ("EVENT_HEADERS", "EventHeaders"),
        ("LOGTOFILE", "LogToFile"),
        ("LOGFILE_NAME", "LogFileName"),
        ("LOGDATETIME", "LogDateTime"),
        ("ReuseEvents", "ReuseEvents"),
        
        // ── EventHub ──
        ("EVENTHUB_CONNECTIONSTRING", "EventHubConnectionString"),
        ("EVENTHUB_NAME", "EventHubName"),
        ("EVENTHUB_NAMESPACE", "EventHubNamespace"),
        ("EVENTHUB_STARTUP_SECONDS", "EventHubStartupSeconds"),
        ("EVENTHUB_MAX_RECONNECT_ATTEMPTS", "EventHubMaxReconnectAttempts"),
        ("EVENTHUB_MAX_UNDRAINED_EVENTS", "MaxUndrainedEvents"),

        // ── Transport / Keep-Alive ──
        ("KeepAliveInitialDelaySecs", "KeepAliveInitialDelaySecs"),
        ("KeepAlivePingIntervalSecs", "KeepAlivePingIntervalSecs"),
        ("KeepAliveIdleTimeoutSecs", "KeepAliveIdleTimeoutSecs"),
        ("EnableMultipleHttp2Connections", "EnableMultipleHttp2Connections"),
        ("MultiConnLifetimeSecs", "MultiConnLifetimeSecs"),
        ("MultiConnIdleTimeoutSecs", "MultiConnIdleTimeoutSecs"),
        ("MultiConnMaxConns", "MultiConnMaxConns"),

        // ── Security ──
        ("IgnoreSSLCert", "IgnoreSSLCert"),

        // ── Identity ──
        ("CONTAINER_APP_NAME", "ContainerApp"),
        ("CONTAINER_APP_REVISION", "Revision"),
        ("CONTAINER_APP_REPLICA_NAME", "ReplicaName"),
        ("Hostname", "HostName"),
        ("RequestIDPrefix", "IDStr"),
    ];


    // Creates a BackendOptions instance by applying environment variable overrides on top of the defaults
    public static ProxyConfig ApplyEnv(Dictionary<string, string> incoming, ProxyConfig defaults)
    {
        // Start from a copy of the defaults; the loop only overwrites keys present in incoming.
        var opts = defaults.DeepClone();

        foreach (var (envVarName, propertyName) in SimpleFields)
        {
            opts.ApplyFieldFromEnv(incoming, envVarName, propertyName);
        }

        opts.AcceptableStatusCodes = ReadEnvironmentVariableOrDefault(incoming, "AcceptableStatusCodes", defaults.AcceptableStatusCodes);
        opts.IterationMode = ReadEnvironmentVariableOrDefault(incoming, "IterationMode", defaults.IterationMode);

        var defaultPriorityWorkers = string.Join(",", defaults.PriorityWorkers.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        opts.PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault(incoming, "PriorityWorkers", defaultPriorityWorkers)));

        var defaultValidateHeaders = string.Join(",", defaults.ValidateHeaders.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        opts.ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault(incoming, "ValidateHeaders", defaultValidateHeaders)));

        ApplyAsyncServiceBusOverrides(incoming, opts, defaults);
        ApplyAsyncBlobStorageOverrides(incoming, opts, defaults);

        // IDStr uses the replica identity for request tracing.
        // Prefer the container-app replica ID over the resolved HostName,
        // since Hostname may be explicitly overridden to a user-friendly value.
        var replicaId = !string.IsNullOrEmpty(opts.ReplicaName) ? opts.ReplicaName : opts.HostName;
        opts.IDStr = $"{opts.IDStr}-{replicaId}-";

        ApplyDerivedSettingsFromConfigNames(
            opts,
            nameof(ProxyConfig.HealthProbeSidecar),
            nameof(ProxyConfig.LoadBalanceMode),
            nameof(ProxyConfig.PriorityKeys),
            nameof(ProxyConfig.PriorityValues),
            nameof(ProxyConfig.ValidateHeaders));

        return opts;
    }

    // Apply in this order
    // 1. Default value from .cs file
    // 2. Value from environment variable (if set)
    // 3. Value from environment variable alias (if set) 
    // 4. Value from App Configuration (if set)
    // public static BackendOptions ParseOptions(Dictionary<string, string> dict)
    // {
    //     EnvVars.Clear();

    //     // calculated values based on logic
    //     var opts = new BackendOptions();

    //     foreach (var (envVarName, propertyName) in SimpleFields)
    //     {
    //         // for all options, uses either the environment or default value
    //         opts.ApplyFieldFromEnv(dict, s_defaults, envVarName, propertyName);
    //     }

    //     opts.AcceptableStatusCodes = ReadEnvironmentVariableOrDefault(dict, "AcceptableStatusCodes", s_defaults.AcceptableStatusCodes);
    //     opts.IterationMode = ReadEnvironmentVariableOrDefault(dict, "IterationMode", s_defaults.IterationMode);

    //     var defaultPriorityWorkers = string.Join(",", s_defaults.PriorityWorkers.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
    //     opts.PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault(dict, "PriorityWorkers", defaultPriorityWorkers)));

    //     var defaultValidateHeaders = string.Join(",", s_defaults.ValidateHeaders.Select(kvp => $"{kvp.Key}={kvp.Value}"));
    //     opts.ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault(dict, "ValidateHeaders", defaultValidateHeaders)));

    //     ApplyAsyncServiceBusOverrides(dict, opts, s_defaults);
    //     ApplyAsyncBlobStorageOverrides(dict, opts, s_defaults);

    //     // IDStr is derived from the prefix + HostName (already resolved via SimpleFields).
    //     opts.IDStr = $"{opts.IDStr}-{opts.HostName}-";

    //     ApplyDerivedSettingsFromConfigNames(
    //         opts,
    //         nameof(BackendOptions.HealthProbeSidecar),
    //         nameof(BackendOptions.LoadBalanceMode),
    //         nameof(BackendOptions.PriorityKeys),
    //         nameof(BackendOptions.PriorityValues),
    //         nameof(BackendOptions.ValidateHeaders));

    //     return opts;
    // }

    // public static IReadOnlyDictionary<string, string> GetParsedEnvVars()
    // {
    //     return new Dictionary<string, string>(EnvVars, StringComparer.OrdinalIgnoreCase);
    // }

    /// <summary>
    /// <summary>
    /// Forwards to <see cref="ProxyConfig.ApplyFieldFromEnv"/>. Kept for backward compatibility.
    /// </summary>
    // public static void ApplyFieldFromEnv(Dictionary<string, string> incomingSettings, ProxyConfig target, string configKey, string propertyName)
    // {
    //     target.ApplyFieldFromEnv(incomingSettings, configKey, propertyName);
    // }

    public static void ApplyDerivedSettings(ProxyConfig backendOptions, params PropertyInfo[] changedProperties)
    {
        if (changedProperties.Length == 0)
        {
            return;
        }

        var changedPropertyNames = new HashSet<string>(
            changedProperties.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        if (changedPropertyNames.Contains(nameof(ProxyConfig.HealthProbeSidecar)))
        {
            ParseHealthProbeSidecarSettings(backendOptions);
        }

        if (changedPropertyNames.Contains(nameof(ProxyConfig.LoadBalanceMode)))
        {
            ValidateLoadBalanceMode(backendOptions);
        }

        if (changedPropertyNames.Contains(nameof(ProxyConfig.PriorityKeys))
            || changedPropertyNames.Contains(nameof(ProxyConfig.PriorityValues)))
        {
            ValidatePrioritySettings(backendOptions, s_defaults);
        }

        if (changedPropertyNames.Contains(nameof(ProxyConfig.ValidateHeaders)))
        {
            ValidateHeaderSettings(backendOptions);
        }
    }

    public static void ApplyDerivedSettingsFromConfigNames(
        ProxyConfig backendOptions,
        params string[] changedConfigNames)
    {
        if (changedConfigNames.Length == 0)
        {
            return;
        }

        var descriptorByConfigName = ConfigMetadata.GetDescriptors()
            .ToDictionary(d => d.ConfigName, d => d.Property, StringComparer.OrdinalIgnoreCase);

        var changedProperties = new List<PropertyInfo>(changedConfigNames.Length);

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
        }

        if (changedProperties.Count > 0)
        {
            ApplyDerivedSettings(backendOptions, [.. changedProperties]);
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

    private static void ApplyAsyncServiceBusOverrides(Dictionary<string, string> env, ProxyConfig opts, ProxyConfig defaults)
    {
        var configStr = ReadEnvironmentVariableOrDefault(env, "AsyncSBConfig", defaults.AsyncSBConfig);
        var (connStr, ns, queue, useMi) = ParseServiceBusConfig(configStr);
        opts.AsyncSBConfig = configStr;
        opts.AsyncSBConnectionString = ReadEnvironmentVariableOrDefault(env, "AsyncSBConnectionString", connStr);
        opts.AsyncSBNamespace = ReadEnvironmentVariableOrDefault(env, "AsyncSBNamespace", ns);
        opts.AsyncSBQueue = ReadEnvironmentVariableOrDefault(env, "AsyncSBQueue", queue);
        opts.AsyncSBUseMI = ReadEnvironmentVariableOrDefault(env, "AsyncSBUseMI", useMi);
    }

    private static void ApplyAsyncBlobStorageOverrides(Dictionary<string, string> env, ProxyConfig opts, ProxyConfig defaults)
    {
        var configStr = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageConfig", defaults.AsyncBlobStorageConfig);
        var (connStr, accountUri, useMi) = ParseBlobStorageConfig(configStr);
        opts.AsyncBlobStorageConfig = configStr;
        opts.AsyncBlobStorageAccountUri = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageAccountUri", accountUri);
        opts.AsyncBlobStorageConnectionString = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageConnectionString", connStr);
        opts.AsyncBlobStorageUseMI = ReadEnvironmentVariableOrDefault(env, "AsyncBlobStorageUseMI", useMi);
    }

    private static void ParseHealthProbeSidecarSettings(ProxyConfig backendOptions)
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

    private static void ValidatePrioritySettings(ProxyConfig backendOptions, ProxyConfig defaults)
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

    private static void ValidateHeaderSettings(ProxyConfig backendOptions)
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

    private static void ValidateLoadBalanceMode(ProxyConfig backendOptions)
    {
        backendOptions.LoadBalanceMode = backendOptions.LoadBalanceMode.Trim().ToLowerInvariant();
        if (backendOptions.LoadBalanceMode != Constants.Latency &&
            backendOptions.LoadBalanceMode != Constants.RoundRobin &&
            backendOptions.LoadBalanceMode != Constants.Random)
        {
            backendOptions.LoadBalanceMode = Constants.Latency;
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

    public static int ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int defaultValue)
    {
        return ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
    }

    public static int[] ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, int[] defaultValues)
    {
        return ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValues);
    }

    public static float ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, float defaultValue)
    {
        return ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
    }

    public static string ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, string defaultValue)
    {
        return ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
    }

    public static IterationModeEnum ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, IterationModeEnum defaultValue)
    {
        string? envValue = env.GetValueOrDefault(variableName)?.Trim();
        if (string.IsNullOrEmpty(envValue) || envValue == ConfigMetadata.DefaultPlaceholder || !Enum.TryParse(envValue, true, out IterationModeEnum value))
        {
            return defaultValue;
        }

        return value;
    }

    public static bool ReadEnvironmentVariableOrDefault(Dictionary<string, string> env, string variableName, bool defaultValue)
    {
        return ReadEnvironmentVariableOrDefaultCore(env, variableName, defaultValue);
    }

    private static int ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, int defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (envValue?.Trim() == ConfigMetadata.DefaultPlaceholder) envValue = null;

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
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigMetadata.DefaultPlaceholder)
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
        if (envValue?.Trim() == ConfigMetadata.DefaultPlaceholder) envValue = null;

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
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigMetadata.DefaultPlaceholder)
        {
            return defaultValue;
        }

        return envValue.Trim();
    }

    private static bool ReadEnvironmentVariableOrDefaultCore(Dictionary<string, string> env, string variableName, bool defaultValue)
    {
        var envValue = env.GetValueOrDefault(variableName);
        if (string.IsNullOrEmpty(envValue) || envValue.Trim() == ConfigMetadata.DefaultPlaceholder)
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

    public static Dictionary<string, string> KVStringPairs(List<string> list, char delimiter = '=')
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

    public static List<string> ToListOfString(string s)
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

        return [.. trimmed.Split(',').Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0)];
    }

    public static List<int> ToListOfInt(string s)
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

        return trimmed.Split(',').Select(p => int.Parse(p.Trim().Trim('"'))).ToList();
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

    /// <summary>
    /// Creates and assigns an <see cref="HttpClient"/> on this <see cref="ProxyConfig"/>
    /// instance, configured from the transport-related properties (keep-alive, HTTP/2, SSL).
    /// </summary>
    public static void ConfigureHttpClient(ProxyConfig backendOptions)
    {
        var safeKeepAliveInitialDelaySecs = Math.Max(1, backendOptions.KeepAliveInitialDelaySecs);
        var safeKeepAlivePingIntervalSecs = Math.Max(1, backendOptions.KeepAlivePingIntervalSecs);

        var retryCount = Math.Max(1, backendOptions.KeepAliveIdleTimeoutSecs / safeKeepAlivePingIntervalSecs);
        var handler = CreateSocketsHandler(safeKeepAliveInitialDelaySecs, safeKeepAlivePingIntervalSecs, retryCount);

        if (backendOptions.EnableMultipleHttp2Connections)
        {
            handler.EnableMultipleHttp2Connections = true;
            handler.PooledConnectionLifetime = TimeSpan.FromSeconds(backendOptions.MultiConnLifetimeSecs);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(backendOptions.MultiConnIdleTimeoutSecs);
            handler.MaxConnectionsPerServer = backendOptions.MultiConnMaxConns;
            handler.ResponseDrainTimeout = TimeSpan.FromSeconds(backendOptions.KeepAliveIdleTimeoutSecs);
            Console.WriteLine("Multiple HTTP/2 connections enabled.");
        }
        else
        {
            handler.EnableMultipleHttp2Connections = false;
            Console.WriteLine("Multiple HTTP/2 connections disabled.");
        }

        // Configure SSL handling
        if (backendOptions.IgnoreSSLCert)
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

    private static SocketsHttpHandler CreateSocketsHandler(int initialDelaySecs, int intervalSecs, int linuxRetryCount)
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
                        BitConverter.GetBytes((uint)(intervalSecs * 1000)).CopyTo(keepAliveValues, 8);

                        s.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, initialDelaySecs);
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSecs);
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, linuxRetryCount);
                        linuxKeepAliveConfigured = true;
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
                        ["IntervalSecs"] = intervalSecs.ToString(),
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
}
