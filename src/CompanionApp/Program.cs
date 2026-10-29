using Azure.Identity;
using CompanionApp.Components;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Startup;
using CompanionApp.Tokenomics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.StreamProcessor;
using System.Text;

internal static partial class Program
{
    public static int DEBUGLEVEL =0;

    internal static readonly StringBuilder checkpoints = new StringBuilder("InitializeAsync: entered");
    static string lastStage = "entered";

    internal static void checkpointHelper(string action, string stage)
    {
        if (DEBUGLEVEL == 1 && lastStage != stage)
        {
            checkpoints.Append($" Entering stage: {stage}");
        } else if (DEBUGLEVEL >= 2)
        {
            checkpoints.Append($"; {action} ");
        }
        lastStage = stage;
    }

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "{Checkpoint}")]
    internal static partial void LogCheckpoint(ILogger logger, string checkpoint);

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
        var applicationArgs = replayOptions.ApplicationArgs
            .Where(arg => !string.Equals(arg, "--uionly", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = WebApplication.CreateBuilder(applicationArgs);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = "custom");
        builder.Logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
        CompanionAppStartup.AddModelConfigFiles(builder);
        StartupDI(builder, replayOptions, uiOnly);
        var app = builder.Build();
        if (uiOnly)
        {
            app.Logger.LogInformation(
                "UI-only mode: profile storage check and configuration loading are disabled.");
        }
        else
        {
            var (drafts, defaultedProfileKeys) = await StartupAppConfig.InitializeAsync(app);
            await StartupStorageBlob.CheckProfileStorageAsync(app);
            await StartupStorageBlob.CreateDefaultProfileBlobsAsync(app, drafts, defaultedProfileKeys);
        }
        await CompanionAppStartup.InitializeAsync(app);
        StartupHttpPipeline(app);
        app.Run();
    }

    private static void StartupDI(
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
        builder.Services.AddSingleton<ImageSyncService>();
        builder.Services.AddSingleton(serviceProvider => new Azure.ResourceManager.ArmClient(
            serviceProvider.GetRequiredService<DefaultAzureCredential>()));
        builder.Services.AddHttpClient(nameof(ContainerAppManagementService), client => {
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
            AllowAutoRedirect = false
        });
        builder.Services.AddSingleton<ContainerAppManagementService>();
        builder.Services.AddSingleton<ChatHistoryStore>();
        builder.Services.AddSingleton<ChatConversationStore>();
        builder.Services.AddSingleton<EventHubMonitorStore>();

        TokenomicsStartup.ConfigureServices(builder, replayOptions, uiOnly);
        builder.Services.AddSingleton<CompanionApp.Observability.ObservabilityDashboardStore>();
        builder.Services.AddHostedService(services =>
            services.GetRequiredService<CompanionApp.Observability.ObservabilityDashboardStore>());
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

    private static void StartupHttpPipeline(WebApplication app)
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
}