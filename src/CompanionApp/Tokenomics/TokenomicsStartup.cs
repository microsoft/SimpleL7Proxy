using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Tokenomics;
using Microsoft.Extensions.DependencyInjection;

namespace CompanionApp.Startup;

internal static class TokenomicsStartup
{
    internal static void ConfigureServices(
        WebApplicationBuilder builder,
        TokenomicsReplayOptions replayOptions,
        bool uiOnly)
    {
        var eventHubSection = builder.Configuration.GetSection(EventHubMonitorOptions.SectionName);
        var eventHubEnabled = eventHubSection.GetValue<bool>("eventhub_enabled", true);
        var localEventFilePath = eventHubSection.GetValue<string>("LocalFilePath");

        if (replayOptions.FileName is not null)
        {
            builder.Services.AddSingleton(replayOptions);
            builder.Services.AddSingleton<TokenomicsDashboardStore>(services =>
            {
                var store = new TokenomicsDashboardStore(
                    services.GetRequiredService<ILogger<TokenomicsDashboardStore>>());
                // Don't call Update() here - let the timer create sample data
                // The store starts with Sample snapshots initialized for all periods
                return store;
            });
            builder.Services.AddHostedService<TokenomicsEventReplay>();
        }
        else
        {
            builder.Services.AddSingleton<TokenomicsDashboardStore>();
        }

        builder.Services.AddSingleton<ProxyMetricsCatalog>();

        if (!uiOnly
            && replayOptions.FileName is null
            && (eventHubEnabled || !string.IsNullOrWhiteSpace(localEventFilePath)))
        {
            builder.Services.AddSingleton(replayOptions);
            builder.Services.AddSingleton<TokenomicsEventReplay>();
            builder.Services.AddHostedService(
                services => services.GetRequiredService<TokenomicsEventReplay>());
            builder.Services.AddHostedService<EventHubReader>();
        }
    }
}
