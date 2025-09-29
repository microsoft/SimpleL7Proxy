using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
//using SimpleL7Proxy.EventGrid;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.BackupAPI;
using SimpleL7Proxy.Feeder;


using System.Net;
using System.Text;

namespace SimpleL7Proxy;


// This code serves as the entry point for the .NET application.
// It sets up the necessary configurations, including logging and telemetry.
// The Main method is asynchronous and initializes the application, 
// setting up logging and loading backend options for further processing.

// The reads all the environment variables and sets up dependency injection for the application.
// After reading the configuration, it starts up the backend pollers and eventhub client.
// Once the backend indicates that it is ready, it starts up the server listener and worker tasks.

// a single cancelation token is shared and used to signal the application to shut down.

public class Program
{
    
    public static async Task Main(string[] args)
    {
        Banner.Display();

        var logLevel = GetLogLevelFromEnvironment();

        var startupLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.FormatterName = "custom");
            builder.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
            builder.SetMinimumLevel(logLevel);
        });

        var startupLogger = startupLoggerFactory.CreateLogger<Program>();

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options => options.FormatterName = "custom");
                logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();

                logging.SetMinimumLevel(logLevel);

            })
            .ConfigureServices((hostContext, services) =>
            {
                ConfigureApplicationInsights(services);
                ConfigureDependencyInjection(services, startupLogger);
            });



        var frameworkHost = hostBuilder.Build();
        //        var serviceProvider = frameworkHost.Services;
        // Perform static initialization after building the host to ensure correct singleton usage
        var serviceProvider = frameworkHost.Services;
        var options = serviceProvider.GetRequiredService<IOptions<BackendOptions>>();
        var eventHubClient = serviceProvider.GetService<IEventClient>();
        var telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();

        // Initialize ProxyEvent with BackendOptions

        ProxyEvent.Initialize(options, eventHubClient, telemetryClient);
    
        try
        {
            //ServiceBusRequestService? serviceBusService = null;

            if (options.Value.AsyncModeEnabled)
            {

                startupLogger.LogWarning("Async mode is enabled. Initializing ServiceBusRequestService and AsyncWorker.");
                var serviceBusRequestService = serviceProvider.GetRequiredService<IServiceBusRequestService>();
                var backupAPIService = serviceProvider.GetRequiredService<IBackupAPIService>();
                RequestData.InitializeServiceBusRequestService(serviceBusRequestService, backupAPIService);

                //_ = serviceBusService.StartAsync(CancellationToken.None);

                // AsyncWorker.Initialize(
                //     serviceProvider.GetRequiredService<BlobWriter>(),
                //     serviceProvider.GetRequiredService<ILogger<AsyncWorker>>()
                // );
            }
            else
            {
                startupLogger.LogError("Async mode is disabled.");
            }
        }
        catch (Exception ex)
        {
            startupLogger.LogError(ex, "Failed to initialize ServiceBusRequestService");
        }

        try
        {
            await frameworkHost.RunAsync();
        }
        catch (OperationCanceledException)
        {
            startupLogger.LogDebug("framework Host RunAsync Operation was canceled.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            startupLogger.LogError($"Main: An unexpected error occurred: {e.Message}");
        }
    }

    private static LogLevel GetLogLevelFromEnvironment()
    {
        var logLevelString = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information";
        var l =  Enum.TryParse<LogLevel>(logLevelString, true, out var logLevel) ? logLevel : LogLevel.Information;

        // This should always be visible as it's critical startup information
        Console.WriteLine($"Log level set to: {l}");

        return l;
    }

    private static void ConfigureApplicationInsights(IServiceCollection services)
    {
        var aiConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";
        if (!string.IsNullOrEmpty(aiConnectionString))
        {
            // Register Application Insights
            services.AddApplicationInsightsTelemetryWorkerService(options =>
            {
                options.ConnectionString = aiConnectionString;
                options.EnableAdaptiveSampling = false; // Disable sampling to ensure all your custom telemetry is sent
            });

            // Configure telemetry to filter out duplicate logs
            services.Configure<TelemetryConfiguration>(config =>
            {
                config.TelemetryProcessorChainBuilder.Use(next => new RequestFilterTelemetryProcessor(next));
                config.TelemetryProcessorChainBuilder.Build();
            });

            // Note: logging isn't fully configured yet
            Console.WriteLine("AppInsights initialized with custom request tracking");
        }
    }

    private static void ConfigureDependencyInjection(IServiceCollection services, ILogger startupLogger)
    {


        // Register TelemetryClient
        services.AddSingleton<TelemetryClient>();
        bool.TryParse(Environment.GetEnvironmentVariable("LOGTOFILE"), out var log_to_file);

        if (log_to_file)
        {
            var logFileName = Environment.GetEnvironmentVariable("LOGFILE_NAME") ?? "events.log";
            services.AddProxyEventLogFileClient(logFileName, Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING"));

        }
        else
        {
            var eventHubConnectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING");
            var eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");

            services.AddSingleton(new EventHubConfig(eventHubConnectionString!, eventHubName!));
            services.AddProxyEventClient(eventHubConnectionString, eventHubName, Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING"));
        }

        services.AddBackendHostConfiguration(startupLogger);

        services.AddSingleton<IBlobWriterFactory, BlobWriterFactory>();
        services.AddSingleton<IBlobWriter>(provider =>
        {
            var factory = provider.GetRequiredService<IBlobWriterFactory>();
            return factory.CreateBlobWriter();
        });

        services.AddTransient<IAsyncWorkerFactory, AsyncWorkerFactory>();

        // services.AddSingleton<BlobWriter>(provider =>
        // {
        //     var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<BackendOptions>>();
        //     return new BlobWriter(optionsMonitor);
        // });

        services.AddSingleton<IUserPriorityService, UserPriority>();
        services.AddSingleton<UserProfile>();
        services.AddSingleton<IUserProfileService>(provider => provider.GetRequiredService<UserProfile>());

        services.AddSingleton<IBackendService, Backends>();
        services.AddSingleton<Server>();
        services.AddSingleton<ConcurrentSignal<RequestData>>();
        services.AddSingleton<IConcurrentPriQueue<RequestData>, ConcurrentPriQueue<RequestData>>();
        //services.AddSingleton<ProxyStreamWriter>();
        services.AddSingleton<IBackendHostHealthCollection, BackendHostHealthCollection>();
        // services.AddSingleton<IBackgroundWorker, BackgroundWorker>();

        services.AddHostedService<Server>(provider => provider.GetRequiredService<Server>());

        // ASYNC RELATED
        // Add storage service registration
        services.AddSingleton<IRequestDataBackupService, RequestDataBackupService>();

        services.AddSingleton<ServiceBusFactory>();
        services.AddSingleton<ServiceBusRequestService>();
        services.AddSingleton<IServiceBusRequestService>(sp => sp.GetRequiredService<ServiceBusRequestService>());
        services.AddHostedService(sp => sp.GetRequiredService<ServiceBusRequestService>());

        services.AddSingleton<IBackupAPIService, BackupAPIService>();
        services.AddHostedService(sp => (BackupAPIService)sp.GetRequiredService<IBackupAPIService>());

        services.AddSingleton<IAsyncFeeder, AsyncFeeder>();
        services.AddSingleton<NormalRequest>();
        services.AddSingleton<OpenAIBackgroundRequest>();
        // services.AddSingleton<IRequestProcessor, NormalRequest>();

        services.AddHostedService(sp => (AsyncFeeder)sp.GetRequiredService<IAsyncFeeder>());

        services.AddHostedService<ProxyWorkerCollection>();
        services.AddTransient(source => new CancellationTokenSource());
        services.AddHostedService<CoordinatedShutdownService>();
        services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());

    }
}
