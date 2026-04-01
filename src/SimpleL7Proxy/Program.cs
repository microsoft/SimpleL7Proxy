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

        DateTime StartTime= DateTime.UtcNow; 
        var startupLoggerFactory = LoggerFactory.Create(ConfigureLogging);
        var startupLogger = startupLoggerFactory.CreateLogger<Program>();

        // Bootstrap the bootstrapper !!!!
        // We can't even connect to App Config unless we know this
        ProxyConfig defaultBackendOptions = new ProxyConfig
        {
            UseOAuthGov = string.Equals(
                Environment.GetEnvironmentVariable("UseOAuthGov"), "true", StringComparison.OrdinalIgnoreCase),
            AppConfigConnectionString = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING"),
            AppConfigEndpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT"),
            AppConfigLabel = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_LABEL"),
            AppConfigRefreshIntervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS"), out var refreshInterval) ? refreshInterval : 30,
        };
        DefaultCredential defaultCredential = new DefaultCredential(defaultBackendOptions);

        var appConfigBootstrap = new AppConfigService(startupLoggerFactory.CreateLogger<AppConfigService>(), defaultBackendOptions, defaultCredential);
        // Fire off the download — CreateBackendOptions will await completion before reading Settings.
        appConfigBootstrap.Start();

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                ConfigureLogging(logging);
            })
            .ConfigureServices((hostContext, services) =>
            {
                ConfigureDI(services, startupLoggerFactory, appConfigBootstrap, defaultCredential);
            });

        var frameworkHost = hostBuilder.Build();
        //        var serviceProvider = frameworkHost.Services;
        // Perform static initialization after building the host to ensure correct singleton usage
        var serviceProvider = frameworkHost.Services;

        var options = serviceProvider.GetRequiredService<IOptions<ProxyConfig>>();
        Banner.Display(options.Value);

        appConfigBootstrap.Notifier       = serviceProvider.GetRequiredService<ConfigChangeNotifier>();
        appConfigBootstrap.HostCollection = serviceProvider.GetRequiredService<IHostHealthCollection>();

        var eventClient = serviceProvider.GetService<IEventClient>();
        var telemetryClient = serviceProvider.GetService<TelemetryClient>();
        var backendTokenProvider = serviceProvider.GetRequiredService<BackendTokenProvider>();

        // Initialize static logger for all stream processors
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var streamProcessorLogger = loggerFactory.CreateLogger("StreamProcessor");
        BaseStreamProcessor.SetLogger(streamProcessorLogger);

        // Initialize ProxyEvent with BackendOptions

        var commonEventData = serviceProvider.GetRequiredService<ICommonEventData>();
        serviceProvider.GetRequiredService<ProxyEventInitializer>();

        // Initialize HostConfig with all required dependencies including service provider for circuit breaker DI
        HostConfig.Initialize(backendTokenProvider, startupLogger, serviceProvider);

        // Register backends after DI container is built and HostConfig is initialized
        var hostCollection = serviceProvider.GetRequiredService<IHostHealthCollection>();

        ConfigFactory.RegisterBackends(options.Value, null, appConfigBootstrap.WarmSettings, hostCollection);

        // Async
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
        }
        catch (Exception ex)
        {
            startupLogger.LogError(ex, "[ERROR] ✗ ServiceBus initialization failed");
        }

        // Log confirmation once all IHostedService.StartAsync calls have completed.
        // This fires after the framework has started every hosted service (including event loggers).
        var appLifetime = serviceProvider.GetRequiredService<IHostApplicationLifetime>();
        appLifetime.ApplicationStarted.Register(() =>
        {
            var composite = serviceProvider.GetRequiredService<CompositeEventClient>();
            ConfigFactory.OutputEnvVars(options.Value);

            startupLogger.LogInformation("[INIT] ✓ All hosted services started — active event loggers: {Loggers}",
                composite.ClientType);
        });

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
            startupLogger.LogError(e, "[ERROR] ✗ Unexpected startup error: {Message}", e.Message);
            
            var pe = new ProxyEvent();
            pe.Type = EventType.Exception;
            pe.SendEvent();
        }

        // await for the coordinated shutdown to complete before exiting Main, ensuring all cleanup logic runs
        // (RunAsync may return early if the host's ShutdownTimeout expires before StopAsync finishes)
        try {
            var shutdownService = serviceProvider.GetRequiredService<CoordinatedShutdownService>();
            await shutdownService.ShutdownComplete;
        }
        catch (ObjectDisposedException)
        {
            //nop - Shutdown may have completed already or may have timed out, in which case we just exit
        }

        catch (Exception ex)
        {
            //nop - Shutdown may have completed already or may have timed out, in which case we just exit
            startupLogger.LogWarning(ex, "[SHUTDOWN] Coordinated shutdown did not complete gracefully");
        }

        startupLogger.LogInformation("[SHUTDOWN] ✅ SimpleL7Proxy Service Stopped.  Version: {Version}  Runtime: {Runtime}", Banner.VERSION, DateTime.UtcNow - StartTime);
    }

    private static void ConfigureLogging(ILoggingBuilder logging)
    {
        var logLevelString = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information";
        var logLevel = Enum.TryParse<LogLevel>(logLevelString, true, out var l) ? l : LogLevel.Information;

        logging.AddConsole(options => options.FormatterName = "custom");
        logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
        logging.SetMinimumLevel(logLevel);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    }

    private static void ConfigureAppInsights(IServiceCollection services, ProxyConfig options, ILogger startupLogger)
    {
        var aiConnectionString = options.AppInsightsConnectionString;
        if (!string.IsNullOrEmpty(aiConnectionString))
        {
            // Register Application Insights — also adds the ILogger → App Insights provider,
            // so all ILogger output flows to both console and App Insights once the host starts.
            services.AddApplicationInsightsTelemetryWorkerService(options =>
            {
                options.ConnectionString = aiConnectionString;
                options.EnableAdaptiveSampling = false; // Disable sampling to ensure all your custom telemetry is sent
            });

            // Filter out duplicate request telemetry
            services.Configure<TelemetryConfiguration>(config =>
            {
                config.TelemetryProcessorChainBuilder.Use(next => new RequestFilterTelemetryProcessor(next));
                config.TelemetryProcessorChainBuilder.Build();
            });

            startupLogger.LogInformation("[INIT] ✓ AppInsights initialized with custom request tracking");
        }
    }
    private static void ConfigureDI(IServiceCollection services, ILoggerFactory startupLoggerFactory, AppConfigService appConfigBootstrap, DefaultCredential defaultCredential)
    {
        services.AddSingleton(appConfigBootstrap);
        services.AddSingleton(defaultCredential);
        TryAddCompositeEventClient(services);
      
        // register the backend options
        var result = ConfigFactory.CreateOptions(appConfigBootstrap).GetAwaiter().GetResult();

        // create a new logger based on configs loaded from App Config
        var startupLogger = startupLoggerFactory.CreateLogger<Program>();


        // a copy of the defaults
        AppConfigService.DEFAULT_OPTIONS = result.baseOptions;
        var backendOptions = result.envOptions;

        Console.Out.Flush();
        
        ConfigureAppInsights(services, backendOptions, startupLogger);
        services.RegisterBackendOptions(startupLogger, backendOptions);

        // Register event headers and event loggers .. needed for AWS
        RegisterEventHeaders(services, startupLogger, backendOptions);
        RegisterEventLoggers(services, startupLogger, backendOptions, backendOptions.EventLoggers);

        // Register refresh services only if App Configuration was reachable.
        appConfigBootstrap.RegisterServices(services, backendOptions);

        services.AddSingleton<WorkerContext>();

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
            services.AddSingleton<IAsyncFeeder, AsyncFeeder>();
            services.AddHostedService(sp => (AsyncFeeder)sp.GetRequiredService<IAsyncFeeder>());
        }
        else {
            services.AddTransient<IAsyncWorkerFactory, NullAsyncWorkerFactory>();
            services.AddSingleton<IBlobWriter, NullBlobWriter>();
            services.AddSingleton<IRequestDataBackupService, NullRequestDataBackupService>();
            services.AddSingleton<IAsyncFeeder, NullAsyncFeeder>();
        }
        // services.AddSingleton<BlobWriter>(provider =>
        // {
        //     var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<BackendOptions>>();
        //     return new BlobWriter(optionsMonitor);
        // });

        services.AddSingleton<IUserPriorityService, UserPriority>();
        services.AddSingleton<UserProfile>();
        services.AddSingleton<IUserProfileService>(provider => provider.GetRequiredService<UserProfile>());
        services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());

        services.AddSingleton<IRequeueWorker, RequeueDelayWorker>();
        services.AddSingleton<IShutdownParticipant>(sp => (IShutdownParticipant)sp.GetRequiredService<IRequeueWorker>());

        services.AddTransient<ICircuitBreaker, CircuitBreaker>();
        services.AddSingleton<ConfigChangeNotifier>();
        services.AddSingleton<ProxyEventInitializer>();
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

        services.AddSingleton<NormalRequest>();
        services.AddSingleton<OpenAIBackgroundRequest>();

        // services.AddSingleton<IRequestProcessor, NormalRequest>();


        // Stream processor factory - optimized singleton for high-throughput scenarios
        services.AddSingleton<StreamProcessorFactory>();

        // Shared Iterator Registry — requests to the same path share the same iterator for fair distribution
        services.AddSingleton<SharedIteratorRegistry>();
        services.AddSingleton<ISharedIteratorRegistry>(sp => sp.GetRequiredService<SharedIteratorRegistry>());
        services.AddSingleton<IShutdownParticipant>(sp => sp.GetRequiredService<SharedIteratorRegistry>());

        services.AddHostedService<WorkerFactory>();
        services.AddTransient(source => new CancellationTokenSource());
        services.AddSingleton<CoordinatedShutdownService>();
        services.AddHostedService<CoordinatedShutdownService>(sp => sp.GetRequiredService<CoordinatedShutdownService>());
    }

    private static void RegisterEventHeaders(IServiceCollection services, ILogger startupLogger, ProxyConfig backendOptions)
    {
        var registered = false;
        var eventdataclass = backendOptions.EventHeaders;
        try
        {
            var dataType = string.IsNullOrEmpty(eventdataclass)
                ? null
                : typeof(Program).Assembly.GetType(eventdataclass, throwOnError: false);

            if (dataType != null && typeof(ICommonEventData).IsAssignableFrom(dataType))
            {
                var instance = (ICommonEventData)Activator.CreateInstance(dataType, Options.Create(backendOptions))!;
                services.AddSingleton(dataType, instance);
                services.AddSingleton<ICommonEventData>(instance);
                registered = true;
            }
        }
        catch (Exception ex)
        {
            startupLogger.LogWarning(ex, "[CONFIG] Failed to register EventHeaders '{EventDataType}'.", eventdataclass);
        }
        finally
        {
            if (!registered)
            {
                startupLogger.LogWarning("[CONFIG] EventHeaders '{EventDataType}' not found or invalid. Falling back to CommonEventHeaders.", eventdataclass);
                services.AddSingleton<ICommonEventData, CommonEventHeaders>();
            }
        }
    }

    private static void RegisterEventLoggers(IServiceCollection services, ILogger startupLogger, ProxyConfig backendOptions, string? eventLoggersRaw)
    {
        HashSet<string> enabledLoggers;
        if (!string.IsNullOrWhiteSpace(eventLoggersRaw))
        {
            enabledLoggers = new HashSet<string>(
                eventLoggersRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            startupLogger.LogInformation("[CONFIG] EVENT_LOGGERS: {EventLoggers}", string.Join(", ", enabledLoggers));
        }
        else
        {
            enabledLoggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                backendOptions.LogToFile ? "file" : "eventhub"
            };
            startupLogger.LogInformation("[CONFIG] EVENT_LOGGERS not set, falling back to legacy: {EventLoggers}", string.Join(", ", enabledLoggers));
        }

        foreach (var loggername in enabledLoggers)
        {
            if (loggername == "file")
            {
                services.AddSingleton<LogFileEventClient>(svc =>
                    new LogFileEventClient(backendOptions.LogFileName, svc.GetRequiredService<CompositeEventClient>(), svc.GetRequiredService<IOptions<ProxyConfig>>()));
                services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService<LogFileEventClient>());
            }
            else if (loggername == "eventhub")
            {
                services.AddSingleton<EventHubClient>();
                services.AddSingleton<IHostedService>(svc => svc.GetRequiredService<EventHubClient>());
            }
            else
            {
                try
                {
                    var loggerType = typeof(Program).Assembly.GetType(loggername, throwOnError: false);
                    if (loggerType == null || !typeof(IEventClient).IsAssignableFrom(loggerType))
                    {
                        startupLogger.LogWarning("[CONFIG] Event logger type '{LoggerType}' not found or does not implement IEventClient. Skipping.", loggername);
                        continue;
                    }

                    var capturedType = loggerType;
                    services.AddSingleton(capturedType, svc =>
                    {
                        var instance = ActivatorUtilities.CreateInstance(svc, capturedType);
                        startupLogger.LogInformation("[CONFIG] ✓ Instantiated event logger: {LoggerType}", capturedType.Name);
                        return instance;
                    });

                    if (typeof(IHostedService).IsAssignableFrom(capturedType))
                    {
                        services.AddSingleton<IHostedService>(svc => (IHostedService)svc.GetRequiredService(capturedType));
                    }

                    startupLogger.LogInformation("[CONFIG] Registered event logger: {LoggerType}", loggername);
                }
                catch (Exception ex)
                {
                    startupLogger.LogWarning(ex, "[CONFIG] Failed to register event logger '{LoggerType}'. Skipping.", loggername);
                }
            }
        }
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
