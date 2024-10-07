using Microsoft.Extensions.Configuration;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using test.nullserver.config;

namespace test.nullserver.nullserver
{
    public class Server : ServerBase
    {
        private readonly ConfigBuilder _configBuilder;
        private readonly HttpClient _httpClient;

        public Server(ConfigBuilder configBuilder, HttpClient httpClient) : base()
        {
            _configBuilder = configBuilder;
            _httpClient = httpClient;

            // Read the config
            InitializeServer();
        }

        private void InitializeServer()
        {
            // Use the settings to initialize the server
            Console.WriteLine($"Port : {_configBuilder.Port}");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Start the server
            Console.WriteLine("Server started");

            var _listeningUrl = $"http://+:{_configBuilder.Port}/"; // Changed to HTTP for simplicity

            var httpListener = new HttpListener();
            httpListener.Prefixes.Add(_listeningUrl);
            var response_delay = ParseTime(_configBuilder.ResponseDelay);

            try
            {
                httpListener.Start();
                Console.WriteLine($"Listening on {_listeningUrl}");
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine($"HttpListenerException: {ex.Message}");
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                // wait for a request and process it
                try
                {
                    var getContextTask = httpListener.GetContextAsync();
                    var context = await getContextTask;
                    context.Response.KeepAlive = true;
                    //Console.WriteLine($"Received request from {context.Request.RemoteEndPoint}");
                    await Task.Delay(response_delay, cancellationToken);
                    await Task.Run(() => ProcessRequestAsync(context, cancellationToken));
                }
                catch (HttpListenerException ex)
                {
                    Console.WriteLine($"HttpListenerException: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }

            httpListener.Stop();
            Console.WriteLine("Server stopped");
        }

        private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            // Process the request
            //Console.WriteLine($"Processing request from {context.Request.RemoteEndPoint}");

            // Read the request
            var request = context.Request;
            var response = context.Response;

            // Read the request body asynchronously
            using (var reader = new System.IO.StreamReader(request.InputStream))
            {
                var requestBody = await reader.ReadToEndAsync();
                //Console.WriteLine($"Request body: {requestBody}");
            }

            // Write the response with status code 200
            response.StatusCode = 200;
            response.ContentType = "text/plain";

            var responseString = "OK";
            var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;

            using (var output = response.OutputStream)
            {
                await output.WriteAsync(buffer, 0, buffer.Length);
            }

            response.Close();
        }
    }
}