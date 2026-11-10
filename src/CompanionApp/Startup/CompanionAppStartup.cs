using CompanionApp.Components.Shared;
using CompanionApp.Simulated;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Startup;

internal static class CompanionAppStartup
{
    internal static async Task AddModelConfigFiles(WebApplicationBuilder builder)
    {
        var chatModelsUrl = builder.Configuration["CompanionApp:ChatModelsUrl"];
        if (string.IsNullOrWhiteSpace(chatModelsUrl))
        {
            builder.Configuration.AddJsonFile("chat-models.json", optional: false, reloadOnChange: true);
        }
        else
        {
            if (!Uri.TryCreate(chatModelsUrl, UriKind.Absolute, out var chatModelsUri))
            {
                throw new InvalidOperationException("CompanionApp:ChatModelsUrl must be an absolute file://, http://, or https:// URL.");
            }

            if (chatModelsUri.IsFile)
            {
                builder.Configuration.AddJsonFile(chatModelsUri.LocalPath, optional: false, reloadOnChange: true);
            }
            else if (string.Equals(chatModelsUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(chatModelsUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                using var stream = new MemoryStream(await httpClient.GetByteArrayAsync(chatModelsUri));
                builder.Configuration.AddJsonStream(stream);
            }
            else
            {
                throw new InvalidOperationException("CompanionApp:ChatModelsUrl must use file://, http://, or https://.");
            }
        }
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
