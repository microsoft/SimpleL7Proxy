using CompanionApp.Components;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Tokenomics;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.StreamProcessor;
using SimpleL7Proxy.Tokenomics;
using CompanionApp.Simulated;
using CompanionApp.Startup;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        TokenomicsReplayOptions replayOptions;
        try
        {
            replayOptions = ParseCommandLineArguments(args);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 1;
            return;
        }

        var uiOnly = replayOptions.ApplicationArgs.Contains("--uionly", StringComparer.OrdinalIgnoreCase);
        var builder = CreateBuilder(replayOptions);
        ConfigureServices(builder, replayOptions, uiOnly);

        var app = builder.Build();

        await app.Services
            .GetRequiredService<AppConfigurationStartupInitializer>()
            .InitializeAsync(uiOnly);
        await InitializeStoresAsync(app);
        ProxyMetricsSeeder.Seed(app.Services.GetRequiredService<ProxyMetricsCatalog>());
        ConfigureHttpPipeline(app);

        app.Run();
    }

    internal static TokenomicsReplayOptions ParseCommandLineArguments(string[] args)
    {
        var applicationArgs = new List<string>();
        string? replayFileName = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--run", StringComparison.OrdinalIgnoreCase))
            {
                if (args[index].StartsWith("--run=", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Use --run events <filename>.");
                }

                applicationArgs.Add(args[index]);
                continue;
            }

            if (replayFileName is not null
                || index + 2 >= args.Length
                || !args[index + 1].Equals("events", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(args[index + 2])
                || args[index + 2].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Replay requires exactly one --run events <filename>.");
            }

            replayFileName = args[index + 2];
            index += 2;
        }

        return TokenomicsReplayOptions.Load(applicationArgs.ToArray(), replayFileName);
    }

    private static WebApplicationBuilder CreateBuilder(TokenomicsReplayOptions replayOptions)
    {
        var applicationArgs = replayOptions.ApplicationArgs
            .Where(arg => !string.Equals(arg, "--uionly", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = WebApplication.CreateBuilder(applicationArgs);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = "custom");
        builder.Logging.AddConsoleFormatter<CompanionApp.Startup.CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
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

        return builder;
    }

    private static void ConfigureServices(
        WebApplicationBuilder builder,
        TokenomicsReplayOptions replayOptions,
        bool uiOnly)
    {
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddDataProtection()
            .SetApplicationName("chat_tester")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".keys")));

        builder.Services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        });
        builder.Services.AddSingleton<AuthTokenSettings>();
        builder.Services.AddSingleton<UserSettings>();
        builder.Services.AddSingleton<HeaderSettings>();
        builder.Services.AddSingleton<HistorySettings>();
        builder.Services.AddSingleton<ConversationSettings>();
        builder.Services.AddSingleton<RequestDebugSettings>();
        builder.Services.AddSingleton<AutoCollapseSettings>();
        builder.Services.AddSingleton<ModelDefaults>();
        builder.Services.AddSingleton<VisionModelCatalog>();
        builder.Services.AddSingleton(new DefaultAzureCredential(new DefaultAzureCredentialOptions()));
        builder.Services.AddSingleton<AppConfigurationScaffoldService>();
        builder.Services.AddSingleton<AppConfigurationStartupInitializer>();
        builder.Services.AddSingleton<ImageSyncService>();
        builder.Services.AddSingleton<ChatHistoryStore>();
        builder.Services.AddSingleton<ChatConversationStore>();
        builder.Services.AddSingleton<EventHubMonitorStore>();

        TokenomicsStartup.ConfigureServices(builder, replayOptions, uiOnly);
        ConfigureOptions(builder);
    }

    private static void ConfigureOptions(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<UserPreferencesService>();
        builder.Services.Configure<CompanionAppOptions>(
            builder.Configuration.GetSection(CompanionAppOptions.SectionName));
        builder.Services.Configure<CompanionAppOptions>(options =>
        {
            options.AppConfigurationRules = builder.Configuration
                .GetSection($"{CompanionAppOptions.UiSectionName}:AppConfigurationRules")
                .Get<List<AppConfigurationSettingRule>>() ?? new();
            options.Hosts = builder.Configuration
                .GetSection($"{CompanionAppOptions.UiSectionName}:Hosts")
                .Get<AppConfigHostSettings>() ?? new();
            options.Hosts.FieldChoices["processor"] = StreamProcessorFactory.ProcessorNames.ToArray();
        });
        builder.Services.Configure<EventHubMonitorOptions>(
            builder.Configuration.GetSection(EventHubMonitorOptions.SectionName));
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


    private static void ConfigureHttpPipeline(WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute(
            "/not-found",
            createScopeForStatusCodePages: true);
        app.UseHttpsRedirection();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }
}