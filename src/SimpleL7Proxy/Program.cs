using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Backend.Iterators;

using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.StreamProcessor;
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

        // Kick off App Configuration download early so values are ready
        // by the time LoadBackendOptions reads environment variables.
        AppConfigBootstrap.Start(startupLogger);

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostContext, config) =>
            {
                config.AddAzureAppConfigurationWithWarmSupport(startupLogger);
            })
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

        // If Azure App Configuration refresh service is available, force initial download before other startup work.
        var appConfigRefreshService = serviceProvider.GetService<AzureAppConfigurationRefreshService>();
        if (appConfigRefreshService != null)
        {
            await appConfigRefreshService.InitializeAsync(CancellationToken.None);
            var appConfigDictionary = appConfigRefreshService.GetCurrentConfigurationDictionary();
            startupLogger.LogInformation("[INIT] Azure App Configuration dictionary loaded with {Count} keys", appConfigDictionary.Count);
            startupLogger.LogInformation("[INIT] ✓ Azure App Configuration initial fetch completed");
        }

        var options = serviceProvider.GetRequiredService<IOptions<BackendOptions>>();
        var eventClient = serviceProvider.GetService<IEventClient>();
        var telemetryClient = serviceProvider.GetService<TelemetryClient>();
        var backendTokenProvider = serviceProvider.GetRequiredService<BackendTokenProvider>();

        // Initialize static logger for all stream processors
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var streamProcessorLogger = loggerFactory.CreateLogger("StreamProcessor");
        BaseStreamProcessor.SetLogger(streamProcessorLogger);
        startupLogger.LogInformation("[INIT] ✓ Stream processor logger initialized");

        // Initialize ProxyEvent with BackendOptions

        ProxyEvent.Initialize(options, eventClient, telemetryClient);

        // Initialize HostConfig with all required dependencies including service provider for circuit breaker DI
        HostConfig.Initialize(backendTokenProvider, startupLogger, serviceProvider);

        // Register backends after DI container is built and HostConfig is initialized
        ConfigBootstrapper.RegisterBackends(options.Value);

        try
        {
            //ServiceBusRequestService? serviceBusService = null;

            if (options.Value.AsyncModeEnabled)
            {

                startupLogger.LogInformation("[INIT] ✓ Async mode enabled - Initializing ServiceBus and AsyncWorker services");
                var serviceBusRequestService = serviceProvider.GetRequiredService<IServiceBusRequestService>();
                var backupAPIService = serviceProvider.GetRequiredService<IBackupAPIService>();
                var userPriority = serviceProvider.GetRequiredService<IUserPriorityService>();
                RequestData.InitializeServiceBusRequestService(serviceBusRequestService, backupAPIService, userPriority, options.Value);
                
                // Manually start BackupAPIService (not registered as IHostedService for controlled shutdown)
                await backupAPIService.StartAsync(CancellationToken.None);

                //_ = serviceBusService.StartAsync(CancellationToken.None);

                // AsyncWorker.Initialize(
                //     serviceProvider.GetRequiredService<BlobWriter>(),
                //     serviceProvider.GetRequiredService<ILogger<AsyncWorker>>()
                // );
            }
            else
            {
                startupLogger.LogInformation("[INIT] ⚠ Async mode disabled - Running in synchronous mode only");
            }
        }
        catch (Exception ex)
        {
            startupLogger.LogError(ex, "[ERROR] ✗ ServiceBus initialization failed");
        }

        try
        {
            await frameworkHost.RunAsync();
        }
        catch (OperationCanceledException)
        {
            startupLogger.LogDebug("[SHUTDOWN] Application shutdown requested");
        }
        catch (Exception e)
        {
            // Log full exception details including inner exceptions
            Console.WriteLine(e.ToString());
            
            startupLogger.LogError(e, "[ERROR] ✗ Unexpected startup error: {Message}", e.Message);
            var pe = new ProxyEvent();
            pe.Type = EventType.Exception;
            pe.SendEvent();
        }
    }

    private static LogLevel GetLogLevelFromEnvironment()
    {
        var logLevelString = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information";
        var l =  Enum.TryParse<LogLevel>(logLevelString, true, out var logLevel) ? logLevel : LogLevel.Information;

        // This should always be visible as it's critical startup information
        Console.WriteLine($"[CONFIG] Log level: {l}");

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
            Console.WriteLine("[INIT] ✓ AppInsights initialized with custom request tracking");
        }
    }

    private static void ConfigureDependencyInjection(IServiceCollection services, ILogger startupLogger)
    {
        // EVENT_LOGGERS is a comma-separated list of event logger backends to enable.
        // Supported values: "file", "eventhub"
        // Example: EVENT_LOGGERS="file,eventhub" enables both simultaneously.
        // Falls back to legacy LOGTOFILE behaviour when EVENT_LOGGERS is not set.
        var eventLoggersRaw = Environment.GetEnvironmentVariable("EVENT_LOGGERS");
        HashSet<string> enabledLoggers;

        if (!string.IsNullOrWhiteSpace(eventLoggersRaw))
        {
            enabledLoggers = new HashSet<string>(
                eventLoggersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"[CONFIG] EVENT_LOGGERS: {string.Join(", ", enabledLoggers)}");
        }
        else
        {
            // Legacy fallback: LOGTOFILE=true → file, otherwise → eventhub
            bool.TryParse(Environment.GetEnvironmentVariable("LOGTOFILE"), out var log_to_file);
            enabledLoggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                log_to_file ? "file" : "eventhub"
            };
            Console.WriteLine($"[CONFIG] EVENT_LOGGERS not set, falling back to legacy: {string.Join(", ", enabledLoggers)}");
        }

        // Ensure CompositeEventClient is registered before any individual clients
        TryAddCompositeEventClient(services);

        foreach ( var loggername in enabledLoggers)
        {
            if (loggername == "file")
            {
                var logFileName = Environment.GetEnvironmentVariable("LOGFILE_NAME") ?? "eventslog.json";
                services.AddSingleton<LogFileEventClient>(svc =>
                    new LogFileEventClient(logFileName, svc.GetRequiredService<CompositeEventClient>()));
                services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<LogFileEventClient>());
            }
            else if ( loggername == "eventhub")
            {
                // EventHubClient reads its own config from env vars (EVENTHUB_CONNECTIONSTRING, EVENTHUB_NAME, etc.)
                services.AddSingleton<EventHubClient>();
                services.AddSingleton<IHostedService>(svc => svc.GetRequiredService<EventHubClient>());
            }
            else {
                // Reflection fallback: resolve type name within this assembly only (prevents cross-assembly loading)
                var loggerType = typeof(Program).Assembly.GetType(loggername, throwOnError: false);
                if (loggerType == null)
                {
                    startupLogger.LogWarning("[CONFIG] Event logger type '{LoggerType}' not found. Skipping.", loggername);
                    continue;
                }
                
                if (!typeof(IEventClient).IsAssignableFrom(loggerType))
                {
                    startupLogger.LogWarning("[CONFIG] Event logger type '{LoggerType}' does not implement IEventClient. Skipping.", loggername);
                    continue;
                }

                // Register as concrete singleton so DI can resolve constructor dependencies
                services.AddSingleton(loggerType);

                // If it implements IHostedService, register it so the host calls StartAsync
                if (typeof(IHostedService).IsAssignableFrom(loggerType))
                {
                    services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService(loggerType));
                }

                startupLogger.LogInformation("[CONFIG] Registered event logger: {LoggerType}", loggername);
            }

        }

        var backendOptions = ConfigBootstrapper.CreateBackendOptions(startupLogger);
        services.AddBackendHostConfiguration(startupLogger, backendOptions);

        // Wire up Azure App Configuration warm-refresh service (no-op if AZURE_APPCONFIG_ENDPOINT is not set)
        services.AddAzureAppConfigurationWithWarmRefresh(startupLogger);

        if (backendOptions.AsyncModeEnabled)
        {
            services.AddSingleton<IBlobWriterFactory, BlobWriterFactory>();
            // Create the underlying BlobWriter (not registered as IBlobWriter)
            services.AddSingleton<BlobWriter>(provider =>
            {
                var factory = provider.GetRequiredService<IBlobWriterFactory>();
                var blobWriter = factory.CreateBlobWriter() as BlobWriter;
                var logger = provider.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("[INIT] ✓ Underlying BlobWriter created: {BlobWriterType}", blobWriter?.GetType().Name ?? "Unknown");
                return blobWriter!;
            });

            // Configure BlobWriteQueue options
            services.AddSingleton(provider => 
            {
                return new BlobWriteQueueOptions
                {
                    WorkerCount = backendOptions.AsyncBlobWorkerCount,
                    MaxQueueSize = 10000,
                    BatchWaitTimeMs = 100,
                    MaxBatchSize = 25,
                    EnableBatching = true,
                    EnableDeduplication = true,  // Disable deduplication - write all operations
                    MetricsIntervalSeconds = 30
                };
            });

            // Register BlobWriteQueue as singleton (started/stopped explicitly by CoordinatedShutdownService)
            services.AddSingleton<BlobWriteQueue>();

            // Register QueuedBlobWriter as the IBlobWriter implementation (wraps BlobWriter)
            services.AddSingleton<IBlobWriter>(provider =>
            {
                var underlyingWriter = provider.GetRequiredService<BlobWriter>();
                var queue = provider.GetRequiredService<BlobWriteQueue>();
                var logger = provider.GetRequiredService<ILogger<QueuedBlobWriter>>();
                
                // Enable queue for writes - set to false to disable queuing
                var queuedWriter = new QueuedBlobWriter(underlyingWriter, queue, logger, useQueueForWrites: true);
                
                var programLogger = provider.GetRequiredService<ILogger<Program>>();
                programLogger.LogInformation("[INIT] ✓ QueuedBlobWriter initialized (wrapping {UnderlyingType})", 
                    underlyingWriter.GetType().Name);
                
                return queuedWriter;
            });

            services.AddTransient<IAsyncWorkerFactory, AsyncWorkerFactory>();
        }
        else {
            services.AddTransient<IAsyncWorkerFactory, NullAsyncWorkerFactory>();
            services.AddSingleton<IBlobWriter, NullBlobWriter>();
            services.AddSingleton<IRequestDataBackupService, NullRequestDataBackupService>();
        }
        // services.AddSingleton<BlobWriter>(provider =>
        // {
        //     var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<BackendOptions>>();
        //     return new BlobWriter(optionsMonitor);
        // });

        services.AddSingleton<IUserPriorityService, UserPriority>();
        services.AddSingleton<UserProfile>();
        services.AddSingleton<IUserProfileService>(provider => provider.GetRequiredService<UserProfile>());

        services.AddSingleton<IRequeueWorker, RequeueDelayWorker>();

        services.AddTransient<ICircuitBreaker, CircuitBreaker>();
        services.AddSingleton<IBackendService, Backends>();
        services.AddSingleton<Server>();
        services.AddSingleton<ConcurrentSignal<RequestData>>();
        services.AddSingleton<IConcurrentPriQueue<RequestData>, ConcurrentPriQueue<RequestData>>();
        //services.AddSingleton<ProxyStreamWriter>();
        services.AddSingleton<IHostHealthCollection, HostCollectionManager>();
        services.AddSingleton<HealthCheckService>();
        services.AddSingleton<RequestLifecycleManager>();
        services.AddSingleton<EventDataBuilder>();

        services.AddSingleton<BackendTokenProvider>();
        services.AddHostedService<BackendTokenProvider>();
        // services.AddSingleton<IBackgroundWorker, BackgroundWorker>();

        services.AddHostedService<Server>(provider => provider.GetRequiredService<Server>());

        // ProbeServer is managed explicitly by CoordinatedShutdownService to ensure
        // it keeps running until the very end of shutdown (container orchestrator needs healthy probes)
        services.AddSingleton<ProbeServer>();

        // ASYNC RELATED
        // Add storage service registration
        services.AddSingleton<IRequestDataBackupService, RequestDataBackupService>();

        services.AddSingleton<ServiceBusFactory>();
        services.AddSingleton<ServiceBusRequestService>();
        services.AddSingleton<IServiceBusRequestService>(sp => sp.GetRequiredService<ServiceBusRequestService>());

        // Note: BackupAPIService is NOT registered as IHostedService - its lifecycle is controlled
        // explicitly by CoordinatedShutdownService to ensure proper shutdown ordering
        services.AddSingleton<IBackupAPIService, BackupAPIService>();

        services.AddSingleton<IAsyncFeeder, AsyncFeeder>();
        services.AddSingleton<NormalRequest>();
        services.AddSingleton<OpenAIBackgroundRequest>();

        // services.AddSingleton<IRequestProcessor, NormalRequest>();

        services.AddHostedService(sp => (AsyncFeeder)sp.GetRequiredService<IAsyncFeeder>());

        // Stream processor factory - optimized singleton for high-throughput scenarios
        services.AddSingleton<StreamProcessorFactory>();

        // Shared Iterator Registry - conditionally registered based on UseSharedIterators option
        // When enabled, requests to the same path share the same iterator for fair distribution
        services.AddSingleton<ISharedIteratorRegistry>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
            if (!options.UseSharedIterators)
            {
                // Return null - ProxyWorkerCollection handles null gracefully
                return null!;
            }
            var logger = sp.GetRequiredService<ILogger<SharedIteratorRegistry>>();
            return new SharedIteratorRegistry(
                logger,
                options.SharedIteratorTTLSeconds,
                options.SharedIteratorCleanupIntervalSeconds);
        });

        services.AddHostedService<ProxyWorkerCollection>();
        services.AddTransient(source => new CancellationTokenSource());
        services.AddHostedService<CoordinatedShutdownService>();
        services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());
    }

    /// <summary>
    /// Ensures CompositeEventClient is registered exactly once.
    /// </summary>
    private static void TryAddCompositeEventClient(IServiceCollection services)
    {
        if (services.Any(sd => sd.ServiceType == typeof(CompositeEventClient)))
            return;

        services.AddSingleton<CompositeEventClient>();
        services.AddSingleton<IEventClient>(svc => svc.GetRequiredService<CompositeEventClient>());
    }
}
