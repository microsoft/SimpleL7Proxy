using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;
//using SimpleL7Proxy.EventGrid;
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.BlobStorage;


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

        var startupLoggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole(options => options.FormatterName = "custom");
            builder.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
        });

        var startupLogger = startupLoggerFactory.CreateLogger<Program>();

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options => options.FormatterName = "custom");
                logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
            })
            .ConfigureServices((hostContext, services) =>
            {
                ConfigureApplicationInsights(services);
                ConfigureDependencyInjection(services, startupLogger);
            });


        var frameworkHost = hostBuilder.Build();
        //        var serviceProvider = frameworkHost.Services;

        try
        {
            await frameworkHost.RunAsync();
        }
        catch (OperationCanceledException)
        {
            startupLogger.LogInformation("Operation was canceled.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            startupLogger.LogError($"An unexpected error occurred: {e.Message}");
        }
    }

    private static void ConfigureApplicationInsights(IServiceCollection services)
    {
        var aiConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";

        if (!string.IsNullOrEmpty(aiConnectionString))
        {
            services.AddApplicationInsightsTelemetryWorkerService(options =>
                options.ConnectionString = aiConnectionString);

            services.AddApplicationInsightsTelemetry(options =>
                options.EnableRequestTrackingTelemetryModule = true);

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddApplicationInsights(
                    configureTelemetryConfiguration: config => config.ConnectionString = aiConnectionString,
                    configureApplicationInsightsLoggerOptions: options => { });
                loggingBuilder.AddConsole();
            });

            Console.WriteLine("AppInsights initialized");
        }
    }

private static void ConfigureDependencyInjection(IServiceCollection services, ILogger startupLogger)
{
    var eventHubConnectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING");
    var eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");

    //var serviceBusConnectionString = Environment.GetEnvironmentVariable("SERVICEBUS_CONNECTIONSTRING");
    // var blobConnectionString = Environment.GetEnvironmentVariable("BLOB_CONNECTIONSTRING") ?? "";
    // var blobContainerName = Environment.GetEnvironmentVariable("BLOB_CONTAINERNAME") ?? "";
    // services.AddSingleton(provider => new BlobWriter(blobConnectionString, blobContainerName));


    // Register TelemetryClient
    services.AddSingleton<TelemetryClient>();

    services.AddProxyEventClient(eventHubConnectionString, eventHubName, Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING"));
    services.AddBackendHostConfiguration(startupLogger);

    services.AddSingleton<IUserPriorityService, UserPriority>();
    services.AddSingleton<UserProfile>();
    services.AddSingleton<IUserProfileService>(provider => provider.GetRequiredService<UserProfile>());

    services.AddSingleton<IBackendService, Backends>();
    services.AddSingleton<Server>();
    services.AddSingleton<ConcurrentSignal<RequestData>>();
    services.AddSingleton<IConcurrentPriQueue<RequestData>, ConcurrentPriQueue<RequestData>>();
    services.AddSingleton<ProxyStreamWriter>();
    services.AddSingleton<IBackendHostHealthCollection, BackendHostHealthCollection>();
    services.AddHostedService<Server>(provider => provider.GetRequiredService<Server>());

    // services.AddSingleton(sp => new ServiceBusClient(serviceBusConnectionString));
    // services.AddSingleton<ServiceBusSenderFactory>();
    // services.AddSingleton<IServiceBusRequestService, ServiceBusRequestService>();

    // Initialize the static variable in RequestData
    // var serviceProvider = services.BuildServiceProvider();
    // var serviceBusRequestService = serviceProvider.GetRequiredService<IServiceBusRequestService>();
    // RequestData.InitializeServiceBusRequestService(serviceBusRequestService);
    // AsyncWorker.Initialize(serviceProvider.GetRequiredService<BlobWriter>(), serviceProvider.GetRequiredService<ILogger<AsyncWorker>>());

    services.AddHostedService<ProxyWorkerCollection>();
    services.AddTransient(source => new CancellationTokenSource());
    services.AddHostedService<CoordinatedShutdownService>();
    services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());
    // services.AddHostedService<ServiceBusRequestService>();
}
}
