using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.User;

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
            builder.AddConsole(options =>
            {
                options.FormatterName = "custom";
            });
            builder.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
        });

        var startupLogger = startupLoggerFactory.CreateLogger<Program>();

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options =>
                {
                    options.FormatterName = "custom";
                });
                logging.AddConsoleFormatter<CustomConsoleFormatter, SimpleConsoleFormatterOptions>();
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Register the configured BackendOptions instance with DI

                var aiConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";

                services.AddLogging(
                    loggingBuilder =>
                            {
                                if (!string.IsNullOrEmpty(aiConnectionString))
                                {
                                    loggingBuilder.AddApplicationInsights(
                            configureTelemetryConfiguration: (config) => config.ConnectionString = aiConnectionString,
                            configureApplicationInsightsLoggerOptions: (options) => { }
                            );
                                }
                                loggingBuilder.AddConsole();
                            });

                if (aiConnectionString != null)
                {
                    services.AddApplicationInsightsTelemetryWorkerService(
                        (options) => options.ConnectionString = aiConnectionString);
                    services.AddApplicationInsightsTelemetry(options =>
                        {
                            options.EnableRequestTrackingTelemetryModule = true;
                        });
                    Console.WriteLine("AppInsights initialized");
                }

                // Add the proxy event client
                var eventHubConnectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING");
                var eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME");
                services.AddProxyEventClient(eventHubConnectionString, eventHubName, aiConnectionString);
                //var pq = new ConcurrentPriQueue<RequestData>();

                // Add the backend options
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

//                services.AddHostedService<Server>();
                services.AddHostedService<ProxyWorkerCollection>();
                services.AddTransient(source => new CancellationTokenSource());
                // register the shutdown service
                services.AddHostedService<CoordinatedShutdownService>();

                // Initialize User Profiles
                services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());
//                services.AddHostedService<UserProfile>(provider => provider.GetRequiredService<UserProfile>());

            });

        var frameworkHost = hostBuilder.Build();
        var serviceProvider = frameworkHost.Services;

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
}
