using Azure.Core;
using Azure.Identity;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using SimpleL7Proxy.Logging;
using OS = System;
using System.Linq.Expressions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

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
    Program program = new Program();
    private static HttpClient hc = new HttpClient();
    public static TelemetryClient? telemetryClient;
    public static int terminationGracePeriodSeconds = 30;
    private static bool shutdownInitiated = false;

    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    public string OAuthAudience { get; set; } = "";

    static IServer? server;
    static IEventHubClient? eventHubClient;
    static List<Task> allTasks = new List<Task>();
    static Task? ListenerTask;
    static Task? backendPollerTask;

    static IBackendService? backends;
    static Dictionary<string, string> EnvVars = new Dictionary<string, string>();

    public static async Task Main(string[] args)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var backendOptions = LoadBackendOptions();
        Constants.REVISION = backendOptions.Revision;
        Constants.CONTAINERAPP = backendOptions.ContainerApp;

        Task? eventHubTask = null; ;

        RegisterShutdownHandlers();

        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddConsole(options =>
            {
                options.FormatterName = SimpleTimestampConsoleFormatter.FormatterName;
            });
            builder.AddConsoleFormatter<SimpleTimestampConsoleFormatter, ConsoleFormatterOptions>();
            builder.AddFilter("Azure.Identity", LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<Program>();


        var hostBuilder = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
            {
                // Register the configured BackendOptions instance with DI
                services.Configure<BackendOptions>(options =>
                {
                    options.AcceptableStatusCodes = backendOptions.AcceptableStatusCodes;
                    options.CircuitBreakerErrorThreshold = backendOptions.CircuitBreakerErrorThreshold;
                    options.CircuitBreakerTimeslice = backendOptions.CircuitBreakerTimeslice;
                    options.Client = backendOptions.Client;
                    options.ContainerApp = backendOptions.ContainerApp;
                    options.DefaultPriority = backendOptions.DefaultPriority;
                    options.DefaultTTLSecs = backendOptions.DefaultTTLSecs;
                    options.DisallowedHeaders = backendOptions.DisallowedHeaders;
                    options.HostName = backendOptions.HostName;
                    options.Hosts = backendOptions.Hosts;
                    options.IDStr = backendOptions.IDStr;
                    options.LoadBalanceMode = backendOptions.LoadBalanceMode;
                    options.LogAllRequestHeaders = backendOptions.LogAllRequestHeaders;
                    options.LogAllRequestHeadersExcept = backendOptions.LogAllRequestHeadersExcept;
                    options.LogAllResponseHeaders = backendOptions.LogAllResponseHeaders;
                    options.LogAllResponseHeadersExcept = backendOptions.LogAllResponseHeadersExcept;
                    options.LogConsole = backendOptions.LogConsole;
                    options.LogConsoleEvent = backendOptions.LogConsoleEvent;
                    options.LogHeaders = backendOptions.LogHeaders;
                    options.LogPoller = backendOptions.LogPoller;
                    options.LogProbes = backendOptions.LogProbes;
                    options.LookupHeaderName = backendOptions.LookupHeaderName;
                    options.MaxQueueLength = backendOptions.MaxQueueLength;
                    options.OAuthAudience = backendOptions.OAuthAudience;
                    options.PollInterval = backendOptions.PollInterval;
                    options.PollTimeout = backendOptions.PollTimeout;
                    options.Port = backendOptions.Port;
                    options.PriorityKeyHeader = backendOptions.PriorityKeyHeader;
                    options.PriorityKeys = backendOptions.PriorityKeys;
                    options.PriorityValues = backendOptions.PriorityValues;
                    options.PriorityWorkers = backendOptions.PriorityWorkers;
                    options.RequiredHeaders = backendOptions.RequiredHeaders;
                    options.Revision = backendOptions.Revision;
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.SuspendedUserConfigUrl = backendOptions.SuspendedUserConfigUrl;
                    options.StripHeaders = backendOptions.StripHeaders;
                    options.TerminationGracePeriodSeconds = backendOptions.TerminationGracePeriodSeconds;
                    options.Timeout = backendOptions.Timeout;
                    options.TimeoutHeader = backendOptions.TimeoutHeader;
                    options.TTLHeader = backendOptions.TTLHeader;
                    options.UniqueUserHeaders = backendOptions.UniqueUserHeaders;
                    options.UseOAuth = backendOptions.UseOAuth;
                    options.UseOAuthGov = backendOptions.UseOAuthGov;
                    options.UseProfiles = backendOptions.UseProfiles;
                    options.UserConfigRequired = backendOptions.UserConfigRequired;
                    options.UserConfigUrl = backendOptions.UserConfigUrl;
                    options.UserConfigRefreshIntervalSecs = backendOptions.UserConfigRefreshIntervalSecs;
                    options.UserPriorityThreshold = backendOptions.UserPriorityThreshold;
                    options.UserProfileHeader = backendOptions.UserProfileHeader;
                    options.UserSoftDeleteTTLMinutes = backendOptions.UserSoftDeleteTTLMinutes;
                    options.ValidateAuthAppFieldName = backendOptions.ValidateAuthAppFieldName;
                    options.ValidateAuthAppID = backendOptions.ValidateAuthAppID;
                    options.ValidateAuthAppIDHeader = backendOptions.ValidateAuthAppIDHeader;
                    options.ValidateAuthAppIDUrl = backendOptions.ValidateAuthAppIDUrl;
                    options.ValidateHeaders = backendOptions.ValidateHeaders;
                    options.Workers = backendOptions.Workers;
                });

                // Register App Insights telemetry, since we're manually logging Requests and Dependencies, disable automatic tracking
                var aiConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";
                if (!string.IsNullOrEmpty(aiConnectionString))
                {
                    // Register Application Insights
                    services.AddApplicationInsightsTelemetryWorkerService(options =>
                    {
                        options.ConnectionString = aiConnectionString;
                        options.EnableAdaptiveSampling = false; // Disable sampling to ensure all your custom telemetry is sent
                       // options.EnableDependencyTrackingTelemetryModule = false; // Disable automatic dependency tracking
                    });    

                    // Configure telemetry to filter out duplicate logs
                    services.Configure<TelemetryConfiguration>(config =>
                    {
                        // Configure ServerTelemetryChannel for aggressive flushing to reduce memory pressure
                        var channel = new Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel.ServerTelemetryChannel();
                        channel.MaxTelemetryBufferCapacity = 100;  // Reduce buffer size (default 500)
                        channel.MaxTransmissionBufferCapacity = 10; // Flush more frequently
                        config.TelemetryChannel = channel;
                        
                        config.TelemetryProcessorChainBuilder.Use(next => new RequestFilterTelemetryProcessor(next));
                        config.TelemetryProcessorChainBuilder.Build();
                    });

                    Console.WriteLine("AppInsights initialized with custom request tracking");
                }
                else
                {
                    // Register a null TelemetryClient to prevent null reference exceptions
                    var nullConfig = TelemetryConfiguration.CreateDefault();
                    nullConfig.DisableTelemetry = true;
                    services.AddSingleton(new TelemetryClient(nullConfig));
                    Console.WriteLine("AppInsights disabled - using null telemetry client");
                }
                
                bool.TryParse(Environment.GetEnvironmentVariable("LOGTOFILE"), out var log_to_file);

                if (log_to_file)
                {
                    var logFileName = Environment.GetEnvironmentVariable("LOGFILE_NAME") ?? "events.json";
                    eventHubClient = new LogFileEventClient(logFileName);
                }
                else
                {
                    try
                    {
                        var eventHubConnectionString = OS.Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") ?? "";
                        var eventHubName = OS.Environment.GetEnvironmentVariable("EVENTHUB_NAME") ?? "";
                        var eventHubNamespace = Environment.GetEnvironmentVariable("EVENTHUB_NAMESPACE") ?? "";
                        if (!string.IsNullOrEmpty(eventHubConnectionString))
                        {
                            eventHubClient = new EventHubClient(eventHubConnectionString, eventHubName);
                        } 
                        else if (!string.IsNullOrEmpty(eventHubNamespace))
                        {
                            if (!eventHubNamespace.Contains("."))
                            {
                                eventHubNamespace = $"{eventHubNamespace}.servicebus.windows.net";
                                Console.WriteLine($"Using fully qualified namespace: {eventHubNamespace}");
                            }

                            eventHubClient = new EventHubClient(eventHubNamespace, eventHubName,  new DefaultAzureCredential());
                        }
                             
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to initialize EventHubClient: {ex.Message}");
                        System.Environment.Exit(1);
                    }
                }
                services.AddSingleton<IEventHubClient>(provider => eventHubClient);

                //services.AddHttpLogging(o => { });
                services.AddSingleton<IBackendOptions>(backendOptions);

                // Initialize User Priority
                var userPriority = new UserPriority();
                userPriority.threshold = backendOptions.UserPriorityThreshold;
                services.AddSingleton<IUserPriority>(userPriority);

                var userProfile = new UserProfile(backendOptions, loggerFactory.CreateLogger<UserProfile>());
                services.AddSingleton<IUserProfile>(userProfile);
                // Initialize User Profiles
                if (backendOptions.UseProfiles && !string.IsNullOrEmpty(backendOptions.UserConfigUrl))
                {
                    userProfile.StartBackgroundConfigReader(cancellationToken);
                }

                services.AddSingleton<IBackendService, Backends>();
                services.AddSingleton<IServer, Server>();
                // Ensure ProbeServer updater runs as a background hosted service
                services.AddSingleton<ProbeServer>();
            });

        var frameworkHost = hostBuilder.Build();
        var serviceProvider = frameworkHost.Services;
        var options = serviceProvider.GetRequiredService<IOptions<BackendOptions>>();
        telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();

        ProxyEvent.Initialize(options, eventHubClient, telemetryClient);
        backends = serviceProvider.GetRequiredService<IBackendService>();
        //ILogger<Program> logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            if (telemetryClient != null)
            {
                Console.SetOut(new AppInsightsTextWriter(telemetryClient, Console.Out, options.Value, false));
                Console.SetError(new AppInsightsTextWriter(telemetryClient, Console.Error, options.Value, true));
            }
        }
        catch (System.InvalidOperationException)
        {
        }

        // Start backend pollers 
        backendPollerTask = backends.Start();

        server = serviceProvider.GetRequiredService<IServer>();
        eventHubTask = eventHubClient?.StartTimer();   // Must shutdown after worker threads are done

        var userProfile = serviceProvider.GetService<IUserProfile>();
        var userPriority = serviceProvider.GetService<IUserPriority>();
        try
        {
            await backends.WaitForStartup(20); // wait for up to 20 seconds for startup
            var queue = server.Start(cancellationToken);
            queue.StartSignaler(cancellationToken);

            var workerPriorities = new Dictionary<int, int>(backendOptions.PriorityWorkers);
            int workerPriority;
            Console.WriteLine($"Worker Priorities: {string.Join(",", workerPriorities)}");

            // The loop creates a number of workers based on backendOptions.Workers.
            // The first worker (wrkrNum == 0) is always a probe worker with priority 0.
            // Subsequent workers are assigned priorities based on the available counts in workerPriorities.
            // If no specific priority is available, the worker is assigned a fallback priority (Constants.AnyPriority).
            for (int wrkrNum = 0; wrkrNum <= backendOptions.Workers; wrkrNum++)
            {
                // Determine the priority for this worker
                if (wrkrNum == 0)
                {
                    workerPriority = 0; // Probe worker
                }
                else
                {
                    workerPriority = workerPriorities.FirstOrDefault(kvp => kvp.Value > 0).Key;
                    if (workerPriority != 0)
                    {
                        workerPriorities[workerPriority]--;
                    }
                    else
                    {
                        workerPriority = Constants.AnyPriority;
                    }
                }
                var pw = new ProxyWorker(cancellationToken, wrkrNum, workerPriority, queue, backendOptions,
                                         userPriority, userProfile, backends, eventHubClient, telemetryClient);
                allTasks.Add(Task.Run(() => pw.TaskRunner(), cancellationToken));
            }

        }
        catch (Exception e)
        {
            Console.WriteLine($"Exiting: {e.Message}"); ;
            System.Environment.Exit(1);
        }

        try
        {
            ListenerTask = server.Run();

            // Shutdown() will call Stop on the eventHubClient
            if (eventHubTask != null)
            {
                await eventHubTask.ConfigureAwait(false);
            }

        }
        catch (Exception e)
        {
            telemetryClient?.TrackException(e);
            Console.Error.WriteLine($"Error: {e.Message}");
            Console.Error.WriteLine($"Stack Trace: {e.StackTrace}");
        }

        try
        {
            // Pass the CancellationToken to RunAsync
            await frameworkHost.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Service Exiting.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            Console.Error.WriteLine($"An unexpected error occurred: {e.Message}");
        }
    }

    static void RegisterShutdownHandlers()
    {
        async Task HandleShutdown()
        {
            if (shutdownInitiated)
            {
                return;
            }

            shutdownInitiated = true;
            Console.WriteLine("############## Shutdown signal received. Initiating shutdown. ##############");
            Console.WriteLine("SIGTERM received. Initiating shutdown...");
            await Shutdown().ConfigureAwait(false);
        }

        PosixSignalRegistration.Create(PosixSignal.SIGTERM, async (ctx) => await HandleShutdown());

        AppDomain.CurrentDomain.ProcessExit += async (s, e) => await HandleShutdown();

        Console.CancelKeyPress += async (sender, e) =>
        {
            e.Cancel = true;
            await HandleShutdown();
        };
    }

    private static async Task Shutdown()
    {
        // ######## BEGIN SHUTDOWN SEQUENCE ########
        cancellationTokenSource.Cancel();
        // Wait for the listener to stop before shutting down the workers:
        if (ListenerTask != null)
        {
            await ListenerTask.ConfigureAwait(false);
        }
        Console.WriteLine($"Waiting for tasks to complete for maximum {terminationGracePeriodSeconds} seconds");
        eventHubClient?.SendData($"Server shutting down:   {ProxyWorker.GetState()}");

        if (server != null)
            server.Queue().Stop();

        var timeoutTask = Task.Delay(terminationGracePeriodSeconds * 1000);
        var allTasksComplete = Task.WhenAll(allTasks);
        var completedTask = await Task.WhenAny(allTasksComplete, timeoutTask);
        if (completedTask == timeoutTask)
        {
            Console.WriteLine($"Tasks did not complete within {terminationGracePeriodSeconds} seconds. Forcing shutdown.");
        }
        else
        {
            Console.WriteLine("All tasks shutdown completed.");
        }

        backends?.Stop(); // Stop the backend pollers
        if (backendPollerTask != null)
        {
            await backendPollerTask.ConfigureAwait(false);
        }
        eventHubClient?.SendData($"Workers Stopped:   {ProxyWorker.GetState()}");
        if (eventHubClient != null)
        {
            await eventHubClient.StopTimer();
        }

        // Flush Application Insights telemetry buffer before shutdown
        if (telemetryClient != null)
        {
            telemetryClient.FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
            Console.WriteLine("Application Insights telemetry flushed");
        }
    }

    private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
    {
        int value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static int[] ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues)
    {
        int[] value = _ReadEnvironmentVariableOrDefault(variableName, defaultValues);
        EnvVars[variableName] = string.Join(",", value);
        return value;
    }
    private static float ReadEnvironmentVariableOrDefault(string variableName, float defaultValue)
    {
        float value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }
    private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
    {
        string value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value;
        return value;
    }
    private static string ReadEnvironmentVariableOrDefault(string altVariableName, string variableName, string defaultValue)
    {
        // Try both variable names and use the first non-empty one
        string? envValue = Environment.GetEnvironmentVariable(variableName)?.Trim() ??
                        Environment.GetEnvironmentVariable(altVariableName)?.Trim();

        // Use default if neither variable is defined
        string result = !string.IsNullOrEmpty(envValue) ? envValue : defaultValue;

        // Record and return the value
        EnvVars[variableName] = result;
        return result;
    }

    private static bool ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
    {
        bool value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    // Reads an environment variable and returns its value as an integer.
    // If the environment variable is not set, it returns the provided default value.
    private static int _ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (!int.TryParse(envValue, out var value))
        {
            //Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return value;
    }

    // Reads an environment variable and returns its value as an integer[].
    // If the environment variable is not set, it returns the provided default value.
    private static int[] _ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(envValue))
        {
            //Console.WriteLine($"Using default: {variableName}: {string.Join(",", defaultValues)}");
            return defaultValues;
        }
        try
        {
            return envValue.Split(',').Select(int.Parse).ToArray();
        }
        catch (Exception)
        {
            //Console.WriteLine($"Could not parse {variableName} as an integer array, using default: {string.Join(",", defaultValues)}");
            return defaultValues;
        }
    }

    // Reads an environment variable and returns its value as a float.
    // If the environment variable is not set, it returns the provided default value.
    private static float _ReadEnvironmentVariableOrDefault(string variableName, float defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (!float.TryParse(envValue, out var value))
        {
            //Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return value;
    }
    // Reads an environment variable and returns its value as a string.
    // If the environment variable is not set, it returns the provided default value.
    private static string _ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(envValue))
        {
            //Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return envValue.Trim();
    }

    // Reads an environment variable and returns its value as a string.
    // If the environment variable is not set, it returns the provided default value.
    private static bool _ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(envValue))
        {
            //Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return envValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    // Converts a List<string> to a dictionary of integers.
    private static Dictionary<int, int> KVIntPairs(List<string> list)
    {
        Dictionary<int, int> keyValuePairs = [];

        foreach (var item in list)
        {
            var kvp = item.Split(':');
            if (int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value))
            {
                keyValuePairs.Add(key, value);
            }
            else
            {
                Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
            }
        }

        return keyValuePairs;
    }

    // Converts a List<string> to a dictionary of stgrings.
    private static Dictionary<string, string> KVStringPairs(List<string> list)
    {
        Dictionary<string, string> keyValuePairs = [];

        foreach (var item in list)
        {
            var kvp = item.Split(':');
            if (kvp.Length == 2)
            {
                keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
            }
            else
            {
                Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
            }
        }

        return keyValuePairs;
    }

    // Converts a comma-separated string to a list of strings.
    private static List<string> ToListOfString(string s)
    {
        if (String.IsNullOrEmpty(s))
            return [];

        return [.. s.Split(',').Select(p => p.Trim())];
    }

    // Converts a comma-separated string to a list of integers.
    private static List<int> ToListOfInt(string s)
    {

        // parse each value in the list
        List<int> ints = new List<int>();
        foreach (var item in s.Split(','))
        {
            if (int.TryParse(item.Trim(), out int value))
            {
                ints.Add(value);
            }
            else
            {
                Console.WriteLine($"Could not parse {item} as an integer, defaulting to 5");
                ints.Add(5);
            }
        }

        return s.Split(',').Select(p => int.Parse(p.Trim())).ToList();
    }

    private static SocketsHttpHandler getHandler(int initialDelaySecs, int IntervalSecs, int linuxRetryCount)
    {
        SocketsHttpHandler handler = new SocketsHttpHandler();
        handler.ConnectCallback = async (ctx, ct) =>
        {
            DnsEndPoint dnsEndPoint = ctx.DnsEndPoint;
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, dnsEndPoint.AddressFamily, ct).ConfigureAwait(false);
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                bool linuxKeepAliveConfigured = false;

                // Basic keep-alive setting - should work on all platforms
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Windows-specific approach using IOControl
                        byte[] keepAliveValues = new byte[12];
                        BitConverter.GetBytes((uint)1).CopyTo(keepAliveValues, 0);           // Turn keep-alive on
                        BitConverter.GetBytes((uint)60000).CopyTo(keepAliveValues, initialDelaySecs);       // 60 seconds before first keep-alive
                        BitConverter.GetBytes((uint)30000).CopyTo(keepAliveValues, IntervalSecs);       // 30 second interval

                        s.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                        //Console.WriteLine("TCP keep-alive settings applied using Windows-specific method");
                    }
                    else if (OperatingSystem.IsLinux())
                    {

                        // Set keep-alive idle time in milliseconds
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, initialDelaySecs);

                        // Set keep-alive interval in milliseconds
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, IntervalSecs);

                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, linuxRetryCount);
                        linuxKeepAliveConfigured = true;

                        //Console.WriteLine($"TCPKEEPALIVETIME set to {initialDelaySecs} seconds (connection idle time before sending probes)");
                        //Console.WriteLine($"TCPKEEPALIVEINTERVAL set to {IntervalSecs} seconds (interval between probes)");
                        // Console.WriteLine($"TCPKEEPALIVERETRYCOUNT set to {linuxRetryCount} probes (max failures before disconnect)");
                    }

                }
                catch (Exception ex)
                {
                    ProxyEvent pe = new()
                    {
                        Type = EventType.Exception,
                        Exception = ex,
                        ["Message"] = "Failed to set TCP keep-alive parameters",
                        ["Host"] = dnsEndPoint.Host,
                        ["Port"] = dnsEndPoint.Port.ToString(),
                        ["InitialDelaySecs"] = initialDelaySecs.ToString(),
                        ["IntervalSecs"] = IntervalSecs.ToString(),
                        ["LinuxRetryCount"] = linuxRetryCount.ToString(),
                        ["linuxKeepAliveConfigured"] = linuxKeepAliveConfigured.ToString()
                    };
                    pe.SendEvent();
                }

                // Connect to the endpoint
                await s.ConnectAsync(addresses, dnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Socket connection error: {ex.Message}");
                s.Dispose();
                throw;
            }
        };

        return handler;
    }

    // Loads backend options from environment variables or uses default values if the variables are not set.
    // It also configures the DNS refresh timeout and sets up an HttpClient instance.
    // If the IgnoreSSLCert environment variable is set to true, it configures the HttpClient to ignore SSL certificate errors.
    // If the AppendHostsFile environment variable is set to true, it appends the IP addresses and hostnames to the /etc/hosts file.
    private static BackendOptions LoadBackendOptions()
    {
        // Read and set the DNS refresh timeout from environment variables or use the default value
        var DNSTimeout = ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 240000);
        var KeepAliveInitialDelaySecs = ReadEnvironmentVariableOrDefault("KeepAliveInitialDelaySecs", 60); // 60 seconds
        var KeepAlivePingIntervalSecs = ReadEnvironmentVariableOrDefault("KeepAlivePingIntervalSecs", 60); // 60 seconds
        var keepAliveDurationSecs = ReadEnvironmentVariableOrDefault("KeepAliveIdleTimeoutSecs", 1200); // 20 minutes

        var EnableMultipleHttp2Connections = ReadEnvironmentVariableOrDefault("EnableMultipleHttp2Connections", false);
        var MultiConnLifetimeSecs = ReadEnvironmentVariableOrDefault("MultiConnLifetimeSecs", 3600); // 1 hours
        var MultiConnIdleTimeoutSecs = ReadEnvironmentVariableOrDefault("MultiConnIdleTimeoutSecs", 300); // 5 minutes
        var MultiConnMaxConns = ReadEnvironmentVariableOrDefault("MultiConnMaxConns", 4000); // 4000 connections

        var retryCount = keepAliveDurationSecs / KeepAlivePingIntervalSecs; // Calculate retry count 
        var handler = getHandler(KeepAliveInitialDelaySecs, KeepAlivePingIntervalSecs, retryCount);

        if (EnableMultipleHttp2Connections)
        {
            handler.EnableMultipleHttp2Connections = true;
            handler.PooledConnectionLifetime = TimeSpan.FromSeconds(MultiConnLifetimeSecs);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(MultiConnIdleTimeoutSecs);
            handler.MaxConnectionsPerServer = MultiConnMaxConns;
            handler.ResponseDrainTimeout = TimeSpan.FromSeconds(keepAliveDurationSecs);
            Console.WriteLine("Multiple HTTP/2 connections enabled.");
        }
        else
        {
            handler.EnableMultipleHttp2Connections = false;
            Console.WriteLine("Multiple HTTP/2 connections disabled.");
        }
        //     PooledConnectionIdleTimeout = TimeSpan.FromSeconds(KeepAliveIdleTimeoutSecs),


        // Configure SSL handling
        if (ReadEnvironmentVariableOrDefault("IgnoreSSLCert", false))
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            };
            Console.WriteLine("Ignoring SSL certificate validation errors.");
        }

        HttpClient _client = new HttpClient(handler);

        string replicaID = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REPLICA_NAME", "01");

        // Create and return a BackendOptions object populated with values from environment variables or default values.
        var backendOptions = new BackendOptions
        {
            AcceptableStatusCodes = ReadEnvironmentVariableOrDefault("AcceptableStatusCodes", new int[] { 200, 401, 403, 404, 408, 410, 412, 417, 400 }),
            CircuitBreakerErrorThreshold = ReadEnvironmentVariableOrDefault("CBErrorThreshold", 50),
            CircuitBreakerTimeslice = ReadEnvironmentVariableOrDefault("CBTimeslice", 60),
            Client = _client,
            ContainerApp = ReadEnvironmentVariableOrDefault("CONTAINER_APP_NAME", "ContainerAppName"),
            DefaultPriority = ReadEnvironmentVariableOrDefault("DefaultPriority", 2),
            DefaultTTLSecs = ReadEnvironmentVariableOrDefault("DefaultTTLSecs", 300),
            DisallowedHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("DisallowedHeaders", "")),
            HostName = ReadEnvironmentVariableOrDefault("Hostname", replicaID),
            Hosts = new List<BackendHost>(),
            IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-",
            LoadBalanceMode = ReadEnvironmentVariableOrDefault("LoadBalanceMode", "latency"), // "latency", "roundrobin", "random"
            LogAllRequestHeaders = ReadEnvironmentVariableOrDefault("LogAllRequestHeaders", false),
            LogAllRequestHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllRequestHeadersExcept", "Authorization")),
            LogAllResponseHeaders = ReadEnvironmentVariableOrDefault("LogAllResponseHeaders", false),
            LogAllResponseHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllResponseHeadersExcept", "Api-Key")),
            LogConsole = ReadEnvironmentVariableOrDefault("LogConsole", true),
            LogConsoleEvent = ReadEnvironmentVariableOrDefault("LogConsoleEvent", false),
            LogHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("LogHeaders", "")),
            LogPoller = ReadEnvironmentVariableOrDefault("LogPoller", true),
            LogProbes = ReadEnvironmentVariableOrDefault("LogProbes", true),
            LookupHeaderName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "UserIDFieldName", "userId"), // migrate from LookupHeaderName
            //LookupHeaderName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "userId"),
            MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
            OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
            PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
            PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
            Port = ReadEnvironmentVariableOrDefault("Port", 80),
            PriorityKeyHeader = ReadEnvironmentVariableOrDefault("PriorityKeyHeader", "S7PPriorityKey"),
            PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
            PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
            PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", "2:1,3:1"))),
            RequiredHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("RequiredHeaders", "")),
            Revision = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REVISION", "revisionID"),
            SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
            SuspendedUserConfigUrl = ReadEnvironmentVariableOrDefault("SuspendedUserConfigUrl", "file:suspended_config.json"),
            StripHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("StripHeaders", "")),
            TerminationGracePeriodSeconds = ReadEnvironmentVariableOrDefault("TERMINATION_GRACE_PERIOD_SECONDS", 30),
            Timeout = ReadEnvironmentVariableOrDefault("Timeout", 1200000), // 20 minutes
            TimeoutHeader = ReadEnvironmentVariableOrDefault("TimeoutHeader", "S7PTimeout"),
            TTLHeader = ReadEnvironmentVariableOrDefault("TTLHeader", "S7PTTL"),
            UniqueUserHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("UniqueUserHeaders", "X-UserID")),
            UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", false),
            UseOAuthGov = ReadEnvironmentVariableOrDefault("UseOAuthGov", false),
            UseProfiles = ReadEnvironmentVariableOrDefault("UseProfiles", false),
            UserConfigUrl = ReadEnvironmentVariableOrDefault("UserConfigUrl", "file:config.json"),
            UserConfigRefreshIntervalSecs = ReadEnvironmentVariableOrDefault("UserConfigRefreshIntervalSecs", 3600), // 1 hour
            UserConfigRequired = ReadEnvironmentVariableOrDefault("UserConfigRequired", false),
            UserPriorityThreshold = ReadEnvironmentVariableOrDefault("UserPriorityThreshold", 0.1f),
            UserProfileHeader = ReadEnvironmentVariableOrDefault("UserProfileHeader", "X-UserProfile"),
            UserSoftDeleteTTLMinutes= ReadEnvironmentVariableOrDefault("UserSoftDeleteTTLMinutes", 6*60),  // 6 hours
            ValidateAuthAppFieldName = ReadEnvironmentVariableOrDefault("ValidateAuthAppFieldName", "authAppID"),
            ValidateAuthAppID = ReadEnvironmentVariableOrDefault("ValidateAuthAppID", false),
            ValidateAuthAppIDHeader = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDHeader", "X-MS-CLIENT-PRINCIPAL-ID"),
            ValidateAuthAppIDUrl = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDUrl", "file:auth.json"),
            ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault("ValidateHeaders", ""))),
            Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
        };

        // This is the MAX TIMEOUT for the HttpClient, not the individual requests.
        backendOptions.Client.Timeout = TimeSpan.FromSeconds(backendOptions.Timeout);

        int i = 1;
        StringBuilder sb = new StringBuilder();
        while (true)
        {

            var hostname = Environment.GetEnvironmentVariable($"Host{i}")?.Trim();
            if (string.IsNullOrEmpty(hostname)) break;

            var probePath = Environment.GetEnvironmentVariable($"Probe_path{i}")?.Trim();
            var ip = Environment.GetEnvironmentVariable($"IP{i}")?.Trim();

            try
            {
                Console.WriteLine($"Found host {hostname} with probe path {probePath} and IP {ip}");
                var bh = new BackendHost(hostname, probePath, ip);
                backendOptions.Hosts.Add(bh);

                sb.AppendLine($"{ip} {bh.host}");

            }
            catch (UriFormatException e)
            {
                Console.WriteLine($"Could not add Host{i} with {hostname} : {e.Message}");
            }

            i++;
        }

        if (Environment.GetEnvironmentVariable("APPENDHOSTSFILE")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
            Environment.GetEnvironmentVariable("AppendHostsFile")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"Adding {sb.ToString()} to /etc/hosts");
            using (StreamWriter sw = File.AppendText("/etc/hosts"))
            {
                sw.WriteLine(sb.ToString());
            }
        }

        // confirm the number of priority keys and values match
        if (backendOptions.PriorityKeys.Count != backendOptions.PriorityValues.Count)
        {
            Console.WriteLine("The number of PriorityKeys and PriorityValues do not match in length, defaulting all values to 5");
            backendOptions.PriorityValues = Enumerable.Repeat(5, backendOptions.PriorityKeys.Count).ToList();
        }

        // confirm that the PriorityWorkers Key's have a corresponding priority keys
        int workerAllocation = 0;
        foreach (var key in backendOptions.PriorityWorkers.Keys)
        {
            if (!(backendOptions.PriorityValues.Contains(key) || key == backendOptions.DefaultPriority))
            {
                Console.WriteLine($"WARNING: PriorityWorkers Key {key} does not have a corresponding PriorityKey");
            }
            workerAllocation += backendOptions.PriorityWorkers[key];
        }

        if (workerAllocation > backendOptions.Workers)
        {
            Console.WriteLine($"WARNING: Worker allocation exceeds total number of workers:{workerAllocation} > {backendOptions.Workers}");
            Console.WriteLine($"Adjusting total number of workers to {workerAllocation}. Fix PriorityWorkers if it isn't what you want.");
            backendOptions.Workers = workerAllocation;
        }

        // if (backendOptions.UniqueUserHeaders.Count > 0)
        // {
        // // Make sure that uniqueUserHeaders are also in the required headers
        // foreach (var header in backendOptions.UniqueUserHeaders)
        // {
        //     if (!backendOptions.RequiredHeaders.Contains(header))
        //     {
        //     Console.WriteLine($"Adding {header} to RequiredHeaders");
        //     backendOptions.RequiredHeaders.Add(header);
        //     }
        // }
        // }

        // If validate headers are set, make sure they are also in the required headers and disallowed headers
        if (backendOptions.ValidateHeaders.Count > 0)
        {
            foreach (var (key, value) in backendOptions.ValidateHeaders)
            {
                Console.WriteLine($"Validating {key} against {value}");
                if (!backendOptions.RequiredHeaders.Contains(key))
                {
                    Console.WriteLine($"Adding {key} to RequiredHeaders");
                    backendOptions.RequiredHeaders.Add(key);
                }
                if (!backendOptions.RequiredHeaders.Contains(value))
                {
                    Console.WriteLine($"Adding {value} to RequiredHeaders");
                    backendOptions.RequiredHeaders.Add(value);
                }
                if (!backendOptions.DisallowedHeaders.Contains(value))
                {
                    Console.WriteLine($"Adding {value} to DisallowedHeaders");
                    backendOptions.DisallowedHeaders.Add(value);
                }
            }
        }

        // Validate LoadBalanceMode case insensitively
        backendOptions.LoadBalanceMode = backendOptions.LoadBalanceMode.Trim().ToLower();
        if (backendOptions.LoadBalanceMode != Constants.Latency &&
            backendOptions.LoadBalanceMode != Constants.RoundRobin &&
            backendOptions.LoadBalanceMode != Constants.Random)
        {
            Console.WriteLine($"Invalid LoadBalanceMode: {backendOptions.LoadBalanceMode}. Defaulting to '{Constants.Latency}'.");
            backendOptions.LoadBalanceMode = Constants.Latency;
        }

        Console.WriteLine("=======================================================================================");
        Console.WriteLine(" #####                                 #       ####### ");
        Console.WriteLine("#     #  # #    # #####  #      ###### #       #    #  #####  #####   ####  #    # #   #");
        Console.WriteLine("#        # ##  ## #    # #      #      #           #   #    # #    # #    #  #  #   # #");
        Console.WriteLine(" #####   # # ## # #    # #      #####  #          #    #    # #    # #    #   ##     #");
        Console.WriteLine("      #  # #    # #####  #      #      #         #     #####  #####  #    #   ##     #");
        Console.WriteLine("#     #  # #    # #      #      #      #         #     #      #   #  #    #  #  #    #");
        Console.WriteLine(" #####   # #    # #      ###### ###### #######   #     #      #    #  ####  #    #   #");
        Console.WriteLine("=======================================================================================");
        Console.WriteLine($"Version: {Constants.VERSION}");
        Console.WriteLine("ENV VARIABLES:");
        OutputEnvVars();

        return backendOptions;
    }

    private static void OutputEnvVars()
    {
        const int keyWidth = 27;
        const int valWidth = 30;
        const int gutterWidth = 4;
        int col = 0;
        string? pendingEntry = null;
        foreach (var kvp in EnvVars)
        {
            string key = kvp.Key;
            string value = kvp.Value;

            // Prepare the entry for this pair
            string entry = $"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}:" +
                        $"{(value.Length > valWidth ? value.Substring(0, valWidth - 3) + "..." : value),-valWidth}";

            if (col == 0)
            {
                // Store the first column entry and wait for the second
                pendingEntry = entry;
                col = 1;
            }
            else
            {
                // If the untrimmed key or value for the second column is too long, print it on its own line
                if (key.Length > keyWidth || value.Length > valWidth)
                {
                    // Print the pending first column entry alone
                    Console.WriteLine(pendingEntry);
                    // Print the long second column entry alone, but obey key/value widths
                    Console.WriteLine($"{(key.Length > keyWidth ? key.Substring(0, keyWidth - 3) + "..." : key),-keyWidth}: {value}");
                    pendingEntry = null;
                    col = 0;
                }
                else
                {
                    // Print both columns on the same line with gutter
                    Console.WriteLine($"{pendingEntry}{new string(' ', gutterWidth)}{entry}");
                    pendingEntry = null;
                    col = 0;
                }
            }
        }
        if (col % 2 != 0)
        {
            Console.WriteLine();
        }
    }
}
