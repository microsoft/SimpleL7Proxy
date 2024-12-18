using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using test.generator.config;
using test.generator.generator_one;

namespace test.generator.generator_one
{
public class Startup
{
    public static async Task Main(string[] args)
    {
            // read the config
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        // create the ConfigBuilder
        var configBuilder = new ConfigBuilder(config);

        // create the HttpClient
        using var httpClient = new HttpClient();

        // create the CancellationTokenSource
        using var cts = new CancellationTokenSource();

        // create the server
        var server = new Server(configBuilder, httpClient);

        // Start the server with the cancellation token
        var serverTask = server.StartAsync(cts.Token);

        cts.Cancel();

        // Wait for the server to stop
        await serverTask;
    }
}
}