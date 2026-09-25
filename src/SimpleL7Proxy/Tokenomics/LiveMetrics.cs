using SimpleL7Proxy.Config;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Tokenomics;

public class LiveMetrics: IConfigChangeSubscriber, IHostedService, IDisposable
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
            _options.TokenomicsEnable = false;
            return;
        }
        _metricsServerUri = new Uri(_options.TokenomicsMetricsServer.TrimEnd('/') + "/tokenomics/metrics/lookup");
    }

    public async Task<ResponseMetric> GetMetric(string UserId, string Model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        if ( !_metricsCache.TryGetValue((UserId, Model), out var metric))
        {
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
            string expiresAt = DateTime.UtcNow.AddSeconds(5).ToString("T");
    
            if (!_expiresAt.TryGetValue(expiresAt, out var _hash))
            {
                _hash = new HashSet<(string, string)>();
                _expiresAt[expiresAt] = _hash;
            }
            _hash.Add((UserId, Model));

        }

        return metric;
    }


    // LOOKATME
    /// <summary>Gets the current token balance for a user and model including the live and rolled-up totals.</summary>
    public async Task<long> GetTokenBalanceAsync(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.DailyInputTokens + pm.DailyOutputTokens;
    }

    // LOOKATME
    public async Task<int> GetDailyUser429CountAsync(TimeSpan window, string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        return pm.DailyUser429;
    }

    // LOOKATME
    /// <summary>Counts 429 responses observed in the supplied window.</summary>
    public async Task<int> GetDailyModel429CountAsync(TimeSpan window, string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        return pm.DailyModel429;
    }

    // LOOKATME
    /// <summary>Gets the average request latency for the supplied window in milliseconds.</summary>
    public async Task<double> GetDailyAverageLatencyMsAsync(TimeSpan window, string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        return pm.DailyAvgLatencyMs;
    }

    // LOOKATME
    /// <summary>Gets the average request latency for the supplied window in milliseconds.</summary>
    public async Task<double> GetMonthlyAverageLatencyMsAsync(TimeSpan window, string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        return pm.MonthlyAvgLatencyMs;
    }

    // LOOKATME
    /// <summary>Gets the count of jailbreak (prompt injection) detections for a user on the current UTC day, excluding queued metrics.</summary>
    public async Task<bool> GetDailyJailbreakAsync(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.IsDailyJailbreakDetected;
    }

    // LOOKATME
    /// <summary>Gets the count of jailbreak (prompt injection) detections for a user in the current UTC month, excluding queued metrics.</summary>
    public async Task<bool> GetMonthlyJailbreakAsync(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.IsMonthlyJailbreakDetected;
    }

    // LOOKATME
    /// <summary>Gets the count of responses with a filtered content category for a user on the current UTC day, excluding queued metrics.</summary>
    public async Task<bool> GetDailyContentFilteredAsync(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.IsDailyContentFiltered;
    }

    // LOOKATME
    /// <summary>Gets the count of responses with a filtered content category for a user in the current UTC month, excluding queued metrics.</summary>
    public async Task<bool> GetMonthlyContentFilteredAsync(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.IsMonthlyContentFiltered;
    }

    public async Task<int> GetMonthlyUser429Async(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

        return pm.MonthlyUser429;
    }

    public async Task<int> GetMonthlyModel429Async(string UserId, string Model)
    {
        var pm = await GetMetric(UserId, Model);

        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Model);

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

    private async Task CleanupExpiredMetricsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(ExpiresCleanupInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string expiresAt = DateTime.UtcNow.ToString("T");

                // remove expired metrics based on the current time
                _expiresAt.TryRemove(expiresAt, out _);

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
