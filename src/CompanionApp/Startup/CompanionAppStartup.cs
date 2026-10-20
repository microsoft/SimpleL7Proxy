using CompanionApp.Components.Shared;
using CompanionApp.Simulated;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Startup;

internal static class CompanionAppStartup
{
    internal static void AddModelConfigFiles(WebApplicationBuilder builder)
    {
        builder.Configuration.AddJsonFile("chat-models.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile(
            $"chat-models.{builder.Environment.EnvironmentName}.json",
            optional: true,
            reloadOnChange: true);
        builder.Configuration.AddJsonFile("vision-models.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile(
            $"vision-models.{builder.Environment.EnvironmentName}.json",
            optional: true,
            reloadOnChange: true);
    }

    internal static async Task InitializeAsync(WebApplication app)
    {
        app.Logger.LogDebug(
            "Starting CompanionApp. Environment={Environment}.",
            app.Environment.EnvironmentName);

        await InitializeStoresAsync(app);
        ProxyMetricsSeeder.Seed(app.Services.GetRequiredService<ProxyMetricsCatalog>());

        app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation(
            "CompanionApp started."));
        app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation(
            "CompanionApp is stopping."));
    }

    private static async Task InitializeStoresAsync(WebApplication app)
    {
        var companionAppOptions = app.Services
            .GetRequiredService<IOptions<CompanionAppOptions>>()
            .Value;

        app.Services.GetRequiredService<HistorySettings>()
            .ApplyDefaultsIfMissing(companionAppOptions.History);
        app.Services.GetRequiredService<ConversationSettings>()
            .ApplyDefaultsIfMissing(companionAppOptions.Conversations);

        await app.Services.GetRequiredService<ChatHistoryStore>().ReloadAsync();
        await app.Services.GetRequiredService<ChatConversationStore>().ReloadAsync();
    }
}
