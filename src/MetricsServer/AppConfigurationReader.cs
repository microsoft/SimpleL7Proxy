using System.Collections.Frozen;
using Azure.Data.AppConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Tokenomics;

namespace MetricsServer;

/// <summary>
/// Downloads and caches App Configuration settings at startup and every 15 minutes.
/// Only complete downloads replace the last successful snapshot.
/// </summary>
public sealed class AppConfigurationReader : BackgroundService
{
    private readonly ILogger<AppConfigurationReader> _logger;
    private readonly MetricsOptions _options;
    private readonly ConfigurationClient? _appConfigurationClient;
    private FrozenDictionary<string, string?> _appConfigurationSettings = FrozenDictionary<string, string?>.Empty;
    private TokenomicsSettings _tokenomicsSettings = new();

    /// <summary>Last complete, successful App Configuration snapshot, keyed by setting name.</summary>
    public IReadOnlyDictionary<string, string?> AppConfigurationSettings => Volatile.Read(ref _appConfigurationSettings);

    /// <summary>Last successfully parsed Tokenomics options; defaults apply until the first success.</summary>
    public TokenomicsSettings TokenomicsSettings => Volatile.Read(ref _tokenomicsSettings);

    /// <summary>Creates a reader using the shared configuration client, when configured.</summary>
    public AppConfigurationReader(
        ILogger<AppConfigurationReader> logger,
        MetricsOptions options,
        ConfigurationClient? appConfigurationClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _appConfigurationClient = appConfigurationClient;
    }

    /// <summary>Polls independently of HTTP serving until the host shuts down.</summary>
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (_appConfigurationClient is null)
        {
            return;
        }

        var label = _options.AppConfigurationLabel;
        var selector = new SettingSelector
        {
            KeyFilter = "*",
            LabelFilter = string.IsNullOrEmpty(label) || label == "\\0" || label == "\0"
                ? "\0"
                : label.Replace("\\", "\\\\").Replace("*", "\\*").Replace(",", "\\,")
        };
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));

        try
        {
            do
            {
                try
                {
                    var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
                    await foreach (var setting in _appConfigurationClient
                        .GetConfigurationSettingsAsync(selector, cancellationToken).ConfigureAwait(false))
                    {
                        settings[setting.Key] = setting.Value;
                    }

                    Volatile.Write(ref _appConfigurationSettings, settings.ToFrozenDictionary(StringComparer.Ordinal));
                    if (settings.TryGetValue("Warm:Tokenomics:Options", out var value))
                    {
                        try
                        {
                            var parsedSettings = new TokenomicsSettings();
                            if (parsedSettings.TryParse(value!))
                            {
                                Volatile.Write(ref _tokenomicsSettings, parsedSettings);
                                if (parsedSettings.ModelCostPerToken.Count == 0)
                                {
                                    _logger.LogInformation("Tokenomics model cost per token: no models configured");
                                }

                                foreach (var (model, pricing) in parsedSettings.ModelCostPerToken
                                    .OrderBy(entry => entry.Key, StringComparer.Ordinal))
                                {
                                    _logger.LogInformation(
                                        "Tokenomics model cost per token: Model={Model} Input={Input} CachedInput={CachedInput} Output={Output}",
                                        model,
                                        pricing.Input,
                                        pricing.CachedInput,
                                        pricing.Output);
                                }
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Failed to parse Warm:Tokenomics:Options; retaining last valid settings. Offending value: {Value}",
                                    value);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Failed to parse Warm:Tokenomics:Options; retaining last valid settings. Offending value: {Value}",
                                value);
                        }
                    }

                    _logger.LogInformation("App Configuration refreshed: {SettingCount} settings", settings.Count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "App Configuration refresh failed; retaining last successful settings");
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }
}
