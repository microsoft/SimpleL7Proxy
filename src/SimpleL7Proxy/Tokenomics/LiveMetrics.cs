using SimpleL7Proxy.Config;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Tokenomics;

public class LiveMetrics : IConfigChangeSubscriber, IHostedService, IDisposable
{
    private volatile bool _metricsCacheisRunning;
    private bool _disposed;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private readonly ProxyConfig _options;

    private static readonly TimeSpan ExpiresCleanupInterval = TimeSpan.FromSeconds(5);
    private Task? _expiresCleanupTask;
    private readonly TokenomicsSettings _tokenomicsSettings;
    private Uri _metricsServerUri = null!;
    private readonly HttpClient httpClient = new HttpClient(); // Placeholder for actual HTTP client usage if needed
    private readonly ConcurrentDictionary<(string UserId, string Model), ResponseMetric> _metricsCache = new();
    private readonly ConcurrentDictionary<string, HashSet<(string UserId, string Model)>> _expiresAt = new();

    public LiveMetrics(ProxyConfig options,
        TokenomicsSettings tokenomicsSettings,
        ConfigChangeNotifier configChangeNotifier)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenomicsSettings = tokenomicsSettings ?? throw new ArgumentNullException(nameof(tokenomicsSettings));

        InitVars();

        configChangeNotifier.Subscribe(this,
           [options => options.TokenomicsEnable,
            options => options.TokenomicsMetricsServer]);
    }

    public Task OnConfigChangedAsync(IReadOnlyList<ConfigChange> changes, ProxyConfig backendOptions, CancellationToken cancellationToken)
    {
        InitVars();
        return Task.CompletedTask;
    }

    public void InitVars()
    {
        if (string.IsNullOrWhiteSpace(_options.TokenomicsMetricsServer))
        {
            Console.WriteLine("Tokenomics metrics server is not configured.");
            _options.TokenomicsEnable = false;
            return;
        }
        _metricsServerUri = new Uri(_options.TokenomicsMetricsServer.TrimEnd('/') + "/tokenomics/metrics/lookup");
    }

    public async Task<ResponseMetric> GetMetric(string UserId, string Model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);
        // bool cacheHit = true;

        if (!_metricsCache.TryGetValue((UserId, Model), out var metric))
        {
            // cacheHit = false;
            // Build new URL
            UriBuilder ub = new UriBuilder(_metricsServerUri);
            ub.Query = $"u={Uri.EscapeDataString(UserId)}&m={Uri.EscapeDataString(Model)}";

            var resp = await httpClient.GetAsync(ub.Uri);
            if (resp.IsSuccessStatusCode)
            {
                var content = await resp.Content.ReadAsStringAsync();
                metric = JsonSerializer.Deserialize<ResponseMetric>(content);
            }
            else
            {
                metric = new();
            }

            _metricsCache[(UserId, Model)] = metric;
            DateTime expiresAt = DateTime.UtcNow.AddSeconds(5);

            if (!_expiresAt.TryGetValue(expiresAt.ToString("O"), out var _hash))  // Use ISO format as key
            {
                _hash = new HashSet<(string, string)>();
                _expiresAt[expiresAt.ToString("O")] = _hash;
            }
            _hash.Add((UserId, Model));

        }

        // Console.WriteLine($"Cache hit: {cacheHit}. Cached metric: " + metric.ToString());

        return metric;
    }

    private async Task CleanupExpiredMetricsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(ExpiresCleanupInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var now = DateTime.UtcNow;
                var expiredKeys = _expiresAt.Keys
                    .Where(k => DateTime.TryParse(k, out var expTime) && expTime <= now)
                    .ToList();

                foreach (var expiredKey in expiredKeys)
                {
                    if (_expiresAt.TryRemove(expiredKey, out var items))
                    {
                        foreach (var item in items)
                        {
                            _metricsCache.TryRemove(item, out _);
                        }
                    }
                }

            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _metricsCacheisRunning = false;
        }

    }

    public long GetDailyTokenBalanceAsync(ResponseMetric pm)
    { 
        return pm.DailyInputTokens + pm.DailyOutputTokens;
    }

    public long GetMonthlyTokenBalanceAsync(ResponseMetric pm)
    {
        return pm.MonthlyInputTokens + pm.MonthlyOutputTokens;
    }
    public int GetDailyUser429CountAsync(ResponseMetric pm)
    {
        return pm.DailyUser429;
    }

    public int GetDailyModel429CountAsync(ResponseMetric pm)
    {
        return pm.DailyModel429;
    }

    public double GetDailyAverageLatencyMsAsync(ResponseMetric pm)
    {
        return pm.DailyAvgLatencyMs;
    }

    public double GetMonthlyAverageLatencyMsAsync(ResponseMetric pm)
    {
        return pm.MonthlyAvgLatencyMs;
    }

    public bool GetDailyJailbreakAsync(ResponseMetric pm)
    {
        return pm.IsDailyJailbreakDetected;
    }

    public bool GetMonthlyJailbreakAsync(ResponseMetric pm)
    {
        return pm.IsMonthlyJailbreakDetected;
    }

    public bool GetDailyContentFilteredAsync(ResponseMetric pm)
    {
        return pm.IsDailyContentFiltered;
    }

    public bool GetMonthlyContentFilteredAsync(ResponseMetric pm)
    {
        return pm.IsMonthlyContentFiltered;
    }

    public int GetMonthlyUser429Async(ResponseMetric pm)
    {
        return pm.MonthlyUser429;
    }

    public int GetMonthlyModel429Async(ResponseMetric pm)
    {
        return pm.MonthlyModel429;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_metricsCacheisRunning)
        {
            return Task.CompletedTask;
        }

        _metricsCacheisRunning = true;
        _expiresCleanupTask = Task.Run(() => CleanupExpiredMetricsAsync(_cancellationTokenSource.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops periodic metric collapsing after draining queued metrics.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_metricsCacheisRunning)
        {
            _cancellationTokenSource.Cancel();
        }
        if (_expiresCleanupTask != null)
        {
            await _expiresCleanupTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Releases resources used by the metrics cache.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}