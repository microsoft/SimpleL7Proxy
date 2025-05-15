using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using OS = System;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Core;
using System.Linq.Expressions;


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
        Task? eventHubTask = null; ;


        RegisterShutdownHandlers();

        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddFilter("Azure.Identity", LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<Program>();


        var hostBuilder = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
            {
                // Register the configured BackendOptions instance with DI
                services.Configure<BackendOptions>(options =>
                {
                    options.AcceptableStatusCodes = backendOptions.AcceptableStatusCodes;
                    options.Client = backendOptions.Client;
                    options.CircuitBreakerErrorThreshold = backendOptions.CircuitBreakerErrorThreshold;
                    options.CircuitBreakerTimeslice = backendOptions.CircuitBreakerTimeslice;
                    options.DefaultPriority = backendOptions.DefaultPriority;
                    options.DefaultTTLSecs = backendOptions.DefaultTTLSecs;
                    options.DisallowedHeaders = backendOptions.DisallowedHeaders;
                    options.HostName = backendOptions.HostName;
                    options.Hosts = backendOptions.Hosts;
                    options.IDStr = backendOptions.IDStr;
                    options.LogAllRequestHeaders = backendOptions.LogAllRequestHeaders;
                    options.LogAllRequestHeadersExcept = backendOptions.LogAllRequestHeadersExcept;
                    options.LogAllResponseHeaders = backendOptions.LogAllResponseHeaders;
                    options.LogAllResponseHeadersExcept = backendOptions.LogAllResponseHeadersExcept;
                    options.LogHeaders = backendOptions.LogHeaders;
                    options.LogProbes = backendOptions.LogProbes;
                    options.LookupHeaderName = backendOptions.LookupHeaderName;
                    options.MaxQueueLength = backendOptions.MaxQueueLength;
                    options.OAuthAudience = backendOptions.OAuthAudience;
                    options.PriorityKeyHeader = backendOptions.PriorityKeyHeader;
                    options.PriorityKeys = backendOptions.PriorityKeys;
                    options.PriorityValues = backendOptions.PriorityValues;
                    options.Port = backendOptions.Port;
                    options.PollInterval = backendOptions.PollInterval;
                    options.PollTimeout = backendOptions.PollTimeout;
                    options.RequiredHeaders = backendOptions.RequiredHeaders;
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.SuspendedUserConfigUrl = backendOptions.SuspendedUserConfigUrl;
                    options.Timeout = backendOptions.Timeout;
                    options.TimeoutHeader = backendOptions.TimeoutHeader;
                    options.TerminationGracePeriodSeconds = backendOptions.TerminationGracePeriodSeconds;
                    options.TTLHeader = backendOptions.TTLHeader;
                    options.UniqueUserHeaders = backendOptions.UniqueUserHeaders;
                    options.UseOAuth = backendOptions.UseOAuth;
                    options.UseOAuthGov = backendOptions.UseOAuthGov;
                    options.UserProfileHeader = backendOptions.UserProfileHeader;
                    options.UseProfiles = backendOptions.UseProfiles;
                    options.UserConfigUrl = backendOptions.UserConfigUrl;
                    options.UserPriorityThreshold = backendOptions.UserPriorityThreshold;
                    options.ValidateHeaders = backendOptions.ValidateHeaders;
                    options.PriorityWorkers = backendOptions.PriorityWorkers;
                    options.ValidateAuthAppID = backendOptions.ValidateAuthAppID;
                    options.ValidateAuthAppFieldName = backendOptions.ValidateAuthAppFieldName;
                    options.ValidateAuthAppIDUrl = backendOptions.ValidateAuthAppIDUrl;
                    options.ValidateAuthAppIDHeader = backendOptions.ValidateAuthAppIDHeader;
                    options.Workers = backendOptions.Workers;
                });

                services.AddLogging(loggingBuilder => loggingBuilder.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>("Category", LogLevel.Information));
                var aiConnectionString = OS.Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";
                if (aiConnectionString != null)
                {
                    services.AddApplicationInsightsTelemetryWorkerService((ApplicationInsightsServiceOptions options) => options.ConnectionString = aiConnectionString);
                    services.AddApplicationInsightsTelemetry(options =>
                    {
                        options.EnableRequestTrackingTelemetryModule = true;
                    });
                    if (aiConnectionString != "")
                        Console.WriteLine("AppInsights initialized");
                }

                EventHubClient? eventHubClient = null;
                try 
                {
                    var eventHubConnectionString = OS.Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") ?? "";
                    var eventHubName = OS.Environment.GetEnvironmentVariable("EVENTHUB_NAME") ?? "";
                    eventHubClient= new EventHubClient(eventHubConnectionString, eventHubName);
                    eventHubTask = eventHubClient.StartTimer();   // Must shutdown after worker threads are done
                }
                catch(Exception ex) {
                    Console.WriteLine($"Failed to initialize EventHubClient: {ex.Message}");
                    System.Environment.Exit(1);
                }

                services.AddSingleton<IEventHubClient>(provider => eventHubClient);
                //services.AddHttpLogging(o => { });
                services.AddSingleton<IBackendOptions>(backendOptions);

                // Initialize User Priority
                var userPriority = new UserPriority();
                userPriority.threshold = backendOptions.UserPriorityThreshold;
                services.AddSingleton<IUserPriority>(userPriority);

                var userProfile = new UserProfile(backendOptions);
                services.AddSingleton<IUserProfile>(userProfile);
                // Initialize User Profiles
                if (backendOptions.UseProfiles && !string.IsNullOrEmpty(backendOptions.UserConfigUrl))
                {
                    userProfile.StartBackgroundConfigReader(cancellationToken);
                }

                services.AddSingleton<IBackendService, Backends>();
                services.AddSingleton<IServer, Server>();
            });

        var frameworkHost = hostBuilder.Build();
        var serviceProvider = frameworkHost.Services;

        backends = serviceProvider.GetRequiredService<IBackendService>();
        //ILogger<Program> logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        try
        {
            Program.telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();
            if (Program.telemetryClient != null)
                Console.SetOut(new AppInsightsTextWriter(Program.telemetryClient, Console.Out));
        }
        catch (System.InvalidOperationException)
        {
        }

        // Start backend pollers 
        backendPollerTask = backends.Start();

        server = serviceProvider.GetRequiredService<IServer>();
        eventHubClient = serviceProvider.GetRequiredService<IEventHubClient>();
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
            Console.WriteLine($"Error: {e.Message}");
            Console.WriteLine($"Stack Trace: {e.StackTrace}");
        }

        try
        {
            // Pass the CancellationToken to RunAsync
            await frameworkHost.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Service Exiting.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            Console.WriteLine($"An unexpected error occurred: {e.Message}");
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
            Console.WriteLine("All tasks completed.");
        }

        backends?.Stop(); // Stop the backend pollers
        if (backendPollerTask != null)
        {
            await backendPollerTask.ConfigureAwait(false);
        }
        eventHubClient?.SendData($"Workers Stopped:   {ProxyWorker.GetState()}");
        eventHubClient?.StopTimer();
    }

    private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue) {
        int value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }

    private static int[] ReadEnvironmentVariableOrDefault(string variableName, int[] defaultValues) {
        int[] value = _ReadEnvironmentVariableOrDefault(variableName, defaultValues);
        EnvVars[variableName] = string.Join(",", value);
        return value;
    }
    private static float ReadEnvironmentVariableOrDefault(string variableName, float defaultValue) {
        float value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value.ToString();
        return value;
    }
    private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue){
        string value = _ReadEnvironmentVariableOrDefault(variableName, defaultValue);
        EnvVars[variableName] = value;
        return value;
    }
    private static bool ReadEnvironmentVariableOrDefault(string variableName, bool defaultValue) {
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

        foreach (var item in list) {
            var kvp = item.Split(':');
            if (int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value)) {
                keyValuePairs.Add(key, value);
            } else {
                Console.WriteLine($"Could not parse {item} as a key-value pair, ignoring");
            }
        }

        return keyValuePairs;
    }

    // Converts a List<string> to a dictionary of stgrings.
    private static Dictionary<string, string> KVStringPairs(List<string> list)
    {
        Dictionary<string, string> keyValuePairs = [];

        foreach (var item in list) {
            var kvp = item.Split(':');
            if (kvp.Length == 2) {
                keyValuePairs.Add(kvp[0].Trim(), kvp[1].Trim());
            } else{
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

    // Loads backend options from environment variables or uses default values if the variables are not set.
    // It also configures the DNS refresh timeout and sets up an HttpClient instance.
    // If the IgnoreSSLCert environment variable is set to true, it configures the HttpClient to ignore SSL certificate errors.
    // If the AppendHostsFile environment variable is set to true, it appends the IP addresses and hostnames to the /etc/hosts file.
    private static BackendOptions LoadBackendOptions()
    {
        // Read and set the DNS refresh timeout from environment variables or use the default value
        var DNSTimeout = ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 120000);
        ServicePointManager.DnsRefreshTimeout = DNSTimeout;

        // Initialize HttpClient and configure it to ignore SSL certificate errors if specified in environment variables.
        HttpClient _client = new HttpClient();
        if (ReadEnvironmentVariableOrDefault("IgnoreSSLCert", false))
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            _client = new HttpClient(handler);
        }

        string replicaID = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REPLICA_NAME", "01");

        // Create and return a BackendOptions object populated with values from environment variables or default values.
        var backendOptions = new BackendOptions
        {
            AcceptableStatusCodes = ReadEnvironmentVariableOrDefault("AcceptableStatusCodes", new int[] { 200, 401, 403, 404, 408, 410, 412, 417, 400 }),
            Client = _client,
            CircuitBreakerErrorThreshold = ReadEnvironmentVariableOrDefault("CBErrorThreshold", 50),
            CircuitBreakerTimeslice = ReadEnvironmentVariableOrDefault("CBTimeslice", 60),
            DefaultPriority = ReadEnvironmentVariableOrDefault("DefaultPriority", 2),
            DefaultTTLSecs = ReadEnvironmentVariableOrDefault("DefaultTTLSecs", 300),
            DisallowedHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("DisallowedHeaders", "")),
            HostName = ReadEnvironmentVariableOrDefault("Hostname", replicaID),
            Hosts = new List<BackendHost>(),
            IDStr = $"{ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P")}-{replicaID}-",
            LogAllRequestHeaders = ReadEnvironmentVariableOrDefault("LogAllRequestHeaders", false),
            LogAllRequestHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllRequestHeadersExcept", "Authorization")),
            LogAllResponseHeaders = ReadEnvironmentVariableOrDefault("LogAllResponseHeaders", false),
            LogAllResponseHeadersExcept = ToListOfString(ReadEnvironmentVariableOrDefault("LogAllResponseHeadersExcept", "Api-Key")),
            LogHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("LogHeaders", "")),
            LogProbes = ReadEnvironmentVariableOrDefault("LogProbes", false),
            LookupHeaderName = ReadEnvironmentVariableOrDefault("LookupHeaderName", "userId"),
            MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
            OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
            Port = ReadEnvironmentVariableOrDefault("Port", 80),
            PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
            PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
            PriorityKeyHeader = ReadEnvironmentVariableOrDefault("PriorityKeyHeader", "S7PPriorityKey"),
            PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
            PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
            RequiredHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("RequiredHeaders", "")),
            SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
            SuspendedUserConfigUrl = ReadEnvironmentVariableOrDefault("SuspendedUserConfigUrl", "file:config.json"),
            Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
            TimeoutHeader = ReadEnvironmentVariableOrDefault("TimeoutHeader", "S7PTimeout"),
            TerminationGracePeriodSeconds = ReadEnvironmentVariableOrDefault("TERMINATION_GRACE_PERIOD_SECONDS", 30),
            TTLHeader = ReadEnvironmentVariableOrDefault("TTLHeader", "S7PTTL"),
            UniqueUserHeaders = ToListOfString(ReadEnvironmentVariableOrDefault("UniqueUserHeaders", "X-UserID")),
            UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", false),
            UseOAuthGov = ReadEnvironmentVariableOrDefault("UseOAuthGov", false),
            UserProfileHeader = ReadEnvironmentVariableOrDefault("UserProfileHeader", "X-UserProfile"),
            UseProfiles = ReadEnvironmentVariableOrDefault("UseProfiles", false),
            UserConfigUrl = ReadEnvironmentVariableOrDefault("UserConfigUrl", "file:config.json"),
            UserPriorityThreshold = ReadEnvironmentVariableOrDefault("UserPriorityThreshold", 0.1f),
            PriorityWorkers = KVIntPairs(ToListOfString(ReadEnvironmentVariableOrDefault("PriorityWorkers", "2:1,3:1"))),
            ValidateHeaders = KVStringPairs(ToListOfString(ReadEnvironmentVariableOrDefault("ValidateHeaders", ""))),
            ValidateAuthAppID = ReadEnvironmentVariableOrDefault("ValidateAuthAppID", false),
            ValidateAuthAppFieldName = ReadEnvironmentVariableOrDefault("ValidateAuthAppFieldName", "authAppID"),
            ValidateAuthAppIDUrl = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDUrl", "file:auth.json"),
            ValidateAuthAppIDHeader = ReadEnvironmentVariableOrDefault("ValidateAuthAppIDHeader", "X-MS-CLIENT-PRINCIPAL-ID"),
            Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
        };

        //backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);

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
