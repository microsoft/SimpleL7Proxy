using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Config;

namespace Tests.Helpers;

/// <summary>
/// Creates <see cref="BaseHostHealth"/> instances with the minimum DI wiring
/// needed by <see cref="HostConfig"/>. Call <see cref="EnsureInitialized"/> once
/// (typically in [ClassInitialize] or [AssemblyInitialize]) before creating hosts.
/// </summary>
public static class TestHostFactory
{
    private static bool _initialized;
    private static readonly object _lock = new();
    private static IServiceProvider? _serviceProvider;

    /// <summary>
    /// Bootstraps the static <see cref="HostConfig.Initialize"/> call that the
    /// production code performs at startup. Safe to call multiple times.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;

            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
            services.Configure<ProxyConfig>(opts =>
            {
                opts.CircuitBreakerErrorThreshold = 100; // high threshold so CB never trips in tests
                opts.CircuitBreakerTimeslice = 60;
                opts.AcceptableStatusCodes = [200, 401, 403, 408, 410, 412, 417, 400];
            });
            services.AddTransient<ICircuitBreaker, CircuitBreaker>();

            _serviceProvider = services.BuildServiceProvider();
            var logger = _serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("TestHostFactory");
            HostConfig.Initialize(null!, logger, _serviceProvider);
            _initialized = true;
        }
    }

    /// <summary>
    /// Creates a <see cref="NonProbeableHostHealth"/> (always-healthy) backed by a
    /// direct-mode <see cref="HostConfig"/>.
    /// </summary>
    /// <param name="hostname">
    /// Fully-qualified host, e.g. <c>"https://host-a.example.com"</c>,
    /// or extended config format: <c>"host=https://host-a.example.com;mode=direct;path=/openai/*"</c>.
    /// </param>
    public static NonProbeableHostHealth CreateHost(string hostname)
    {
        EnsureInitialized();
        var logger = _serviceProvider!.GetRequiredService<ILoggerFactory>().CreateLogger("TestHost");
        // Use direct-mode by default so no probe URL is needed
        var configStr = hostname.Contains(';') ? hostname : $"host={hostname};mode=direct";
        var config = new HostConfig(configStr);
        config.Activate();
        return new NonProbeableHostHealth(config, logger);
    }

    /// <summary>
    /// Convenience: creates a list of N direct-mode catch-all hosts named host-0 … host-(n-1).
    /// </summary>
    public static List<BaseHostHealth> CreateHosts(int count, string? pathOverride = null)
    {
        EnsureInitialized();
        var hosts = new List<BaseHostHealth>(count);
        for (int i = 0; i < count; i++)
        {
            var pathPart = pathOverride != null ? $";path={pathOverride}" : "";
            hosts.Add(CreateHost($"host=https://host-{i}.example.com;mode=direct{pathPart}"));
        }
        return hosts;
    }
}
