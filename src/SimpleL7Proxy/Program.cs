
using System.Net;
using System.Text;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SimpleL7Proxy;


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
    private static HttpClient hc = new HttpClient();
    Program program = new Program();
    public static TelemetryClient? telemetryClient;

    static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
    public string OAuthAudience { get; set; } ="";  

    public static async Task Main(string[] args)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var backendOptions = LoadBackendOptions();

        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddFilter("Azure.Identity", LogLevel.Debug);
        });

        var logger = loggerFactory.CreateLogger<Program>();

        Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("Shutdown signal received. Initiating shutdown...");
                e.Cancel = true; // Prevent the process from terminating immediately.
                cancellationTokenSource.Cancel(); // Signal the application to shut down.
            };

        var hostBuilder = Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
            {        
                // Register the configured BackendOptions instance with DI
                services.Configure<BackendOptions>(options =>
                {
                    options.Client = backendOptions.Client;
                    options.DefaultPriority = backendOptions.DefaultPriority;
                    options.DefaultTTLSecs = backendOptions.DefaultTTLSecs;
                    options.HostName = backendOptions.HostName;
                    options.Hosts = backendOptions.Hosts;
                    options.IDStr = backendOptions.IDStr;
                    options.LogHeaders = backendOptions.LogHeaders;
                    options.MaxQueueLength = backendOptions.MaxQueueLength;
                    options.OAuthAudience = backendOptions.OAuthAudience;
                    options.PriorityKeys = backendOptions.PriorityKeys;
                    options.PriorityValues = backendOptions.PriorityValues;
                    options.Port = backendOptions.Port;
                    options.PollInterval = backendOptions.PollInterval;
                    options.PollTimeout = backendOptions.PollTimeout;
                    options.SuccessRate = backendOptions.SuccessRate;
                    options.Timeout = backendOptions.Timeout;
                    options.UseOAuth = backendOptions.UseOAuth;
                    options.Workers = backendOptions.Workers;
                });

                services.AddLogging(loggingBuilder => loggingBuilder.AddFilter<Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider>("Category", LogLevel.Information));
                var aiConnectionString = Environment.GetEnvironmentVariable("APPINSIGHTS_CONNECTIONSTRING") ?? "";
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

                var eventHubConnectionString = Environment.GetEnvironmentVariable("EVENTHUB_CONNECTIONSTRING") ?? "";
                var eventHubName = Environment.GetEnvironmentVariable("EVENTHUB_NAME") ?? "";
                EventHubClient eventHubClient = new(eventHubConnectionString, eventHubName);
                eventHubClient.StartTimer();

                services.AddSingleton<IEventHubClient>(provider => eventHubClient);
                services.AddSingleton<IBackendOptions>(backendOptions);
                services.AddSingleton<IBackendService, Backends>();
                services.AddSingleton<IServer, Server>();
            });
    

        var frameworkHost = hostBuilder.Build();
        var serviceProvider = frameworkHost.Services;
        var backends = serviceProvider.GetRequiredService<IBackendService>();
        try
        {
            telemetryClient = serviceProvider.GetRequiredService<TelemetryClient>();
            if (telemetryClient != null)
                Console.SetOut(new AppInsightsTextWriter(telemetryClient, Console.Out));
        }
        catch (InvalidOperationException)
        {
            //TODO: Handle this exception
        }

        backends.Start(cancellationToken);

        var server = serviceProvider.GetRequiredService<IServer>();
        var eventHubClient = serviceProvider.GetRequiredService<IEventHubClient>();
        IEnumerable<Task> tasks = await QueueWork(
            server,
            eventHubClient,
            backendOptions,
            backends,
            telemetryClient,
            cancellationToken);

        try
        {        
            await server.Run();
            Console.WriteLine("Waiting for tasks to complete for maximum 10 seconds");
            server.Queue().Stop();
            var timeoutTask = Task.Delay(10000); // 10 seconds timeout
            var allTasks = Task.WhenAll(tasks);
            var completedTask = await Task.WhenAny(allTasks, timeoutTask);
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
            Console.WriteLine("Operation was canceled.");
        }
        catch (Exception e)
        {
            // Handle other exceptions that might occur
            Console.WriteLine($"An unexpected error occurred: {e.Message}");
        }
    }

    // Rreads an environment variable and returns its value as an integer.
    // If the environment variable is not set, it returns the provided default value.
    private static int ReadEnvironmentVariableOrDefault(string variableName, int defaultValue)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(variableName), out var value))
        {
            Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return value;
    }

    // Rreads an environment variable and returns its value as a string.
    // If the environment variable is not set, it returns the provided default value.
    private static string ReadEnvironmentVariableOrDefault(string variableName, string defaultValue)
    {
        var envValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(envValue))
        {
            Console.WriteLine($"Using default: {variableName}: {defaultValue}");
            return defaultValue;
        }
        return envValue.Trim();
    }

    // Converts a comma-separated string to a list of strings.
    private static List<string> ToListOfString(string s)
    {
        return s.Split(',').Select(p => p.Trim()).ToList();
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
            } else {
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
        var DNSTimeout= ReadEnvironmentVariableOrDefault("DnsRefreshTimeout", 120000);
        ServicePointManager.DnsRefreshTimeout = DNSTimeout;

        // Initialize HttpClient and configure it to ignore SSL certificate errors if specified in environment variables.
        HttpClient _client = new HttpClient();
        if (Environment.GetEnvironmentVariable("IgnoreSSLCert")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true) {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
            _client = new HttpClient(handler);
        }

        string replicaID = ReadEnvironmentVariableOrDefault("CONTAINER_APP_REPLICA_NAME", "01");

#if DEBUG
        // Load appsettings.json only in Debug mode
        var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

        foreach (var setting in configuration.GetSection("Settings").GetChildren())
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
#endif

        // Create and return a BackendOptions object populated with values from environment variables or default values.
        var backendOptions = new BackendOptions
        {
            Client = _client, 
            DefaultPriority = ReadEnvironmentVariableOrDefault("DefaultPriority", 2),
            DefaultTTLSecs = ReadEnvironmentVariableOrDefault("DefaultTTLSecs", 300),
            HostName = ReadEnvironmentVariableOrDefault("Hostname", "Default"),
            Hosts = new List<BackendHost>(),
            IDStr = ReadEnvironmentVariableOrDefault("RequestIDPrefix", "S7P") + "-" + replicaID +"-",
            LogHeaders = ReadEnvironmentVariableOrDefault("LogHeaders", "").Split(',').Select(x=>x.Trim()).ToList(),
            MaxQueueLength = ReadEnvironmentVariableOrDefault("MaxQueueLength", 10),
            OAuthAudience = ReadEnvironmentVariableOrDefault("OAuthAudience", ""),
            Port = ReadEnvironmentVariableOrDefault("Port", 443),
            PollInterval = ReadEnvironmentVariableOrDefault("PollInterval", 15000),
            PollTimeout = ReadEnvironmentVariableOrDefault("PollTimeout", 3000),
            PriorityKeys = ToListOfString(ReadEnvironmentVariableOrDefault("PriorityKeys", "12345,234")),
            PriorityValues = ToListOfInt(ReadEnvironmentVariableOrDefault("PriorityValues", "1,3")),
            SuccessRate = ReadEnvironmentVariableOrDefault("SuccessRate", 80),
            Timeout = ReadEnvironmentVariableOrDefault("Timeout", 3000),
            UseOAuth = ReadEnvironmentVariableOrDefault("UseOAuth", "false").Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true,
            Workers = ReadEnvironmentVariableOrDefault("Workers", 10),
        };

        backendOptions.Client.Timeout = TimeSpan.FromMilliseconds(backendOptions.Timeout);

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
            Environment.GetEnvironmentVariable("AppendHostsFile")?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true ) {
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

        Console.WriteLine ("=======================================================================================");
        Console.WriteLine(" #####                                 #       ####### ");
        Console.WriteLine("#     #  # #    # #####  #      ###### #       #    #  #####  #####   ####  #    # #   #");
        Console.WriteLine("#        # ##  ## #    # #      #      #           #   #    # #    # #    #  #  #   # #");
        Console.WriteLine(" #####   # # ## # #    # #      #####  #          #    #    # #    # #    #   ##     #");
        Console.WriteLine("      #  # #    # #####  #      #      #         #     #####  #####  #    #   ##     #");
        Console.WriteLine("#     #  # #    # #      #      #      #         #     #      #   #  #    #  #  #    #");
        Console.WriteLine(" #####   # #    # #      ###### ###### #######   #     #      #    #  ####  #    #   #");
        Console.WriteLine ("=======================================================================================");
        Console.WriteLine("Version: 2.0.0");

        return backendOptions;
    }
    
    private static async Task<IEnumerable<Task>> QueueWork(
        IServer server,
        IEventHubClient eventHubClient,
        BackendOptions backendOptions,
        IBackendService backends,
        TelemetryClient? telemetryClient,
        CancellationToken cancellationToken)
    {
        try
        {
            await backends.waitForStartup(20); // wait for up to 20 seconds for startup
            var queue = server.Start(cancellationToken);
            queue.StartSignaler(cancellationToken);

            ProxyStreamWriter proxyStreamWriter = new();
            var queuedTasks = Enumerable.Range(0, backendOptions.Workers)
                .AsQueryable()
                .Select(worker => new ProxyWorker(
                    worker,
                    proxyStreamWriter,
                    queue,
                    backendOptions,
                    backends,
                    eventHubClient,
                    telemetryClient,
                    cancellationToken))
                .Select(t => Task.Run(() => t.TaskRunner(), cancellationToken));
            return [..queuedTasks];
        }
        catch (Exception e)
        {
            Console.WriteLine($"Exiting: {e.Message}");
            Environment.Exit(1);
            throw;
        }
    }
}