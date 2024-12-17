//using Microsoft.AspNetCore.Hosting.Internal;
//using Microsoft.Azure.Amqp.Framing;
using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using test.generator.config;
using Azure.Identity;
using Azure.Core;

namespace test.generator.generator_one
{
    public class Server : ServerBase
    {
        private readonly ConfigBuilder _configBuilder;
        private readonly HttpClient _httpClient;

        private static int _requestCount = 0;
        private readonly object _lock = new object();

        private  DateTimeOffset tokenExpiry = DateTime.MinValue;
        private Azure.Core.AccessToken? token = null;
        private string tokenStr = "";

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
            Console.WriteLine($"Test endpoint: {_configBuilder.TestEndpoint}");
            Console.WriteLine($"Duration: {_configBuilder.DurationSeconds}");
            Console.WriteLine($"Concurrency: {_configBuilder.Concurrency}");
            Console.WriteLine($"Delay: {_configBuilder.InterrunDelay}");

            if (_configBuilder.needsToken())
            {
                Console.WriteLine("Getting token ...");
                try {
                    var tokenStr = GetToken();
                    Console.WriteLine($"Token: {tokenStr}");
                } catch (Exception ex) {
                    Console.WriteLine($"Exception in getting token: {ex.Message}");
                } 
            }
        }

        private string GetToken()
        {
            if (!string.IsNullOrEmpty( tokenStr) ) {
                return tokenStr;
            }

            if (DateTime.Now < token?.ExpiresOn && token is not null)
            {
                return token?.Token;
            }
            
            var tokenEnv = Environment.GetEnvironmentVariable("token");
            if (!string.IsNullOrEmpty(tokenEnv))
            {
                tokenStr = tokenEnv;
                return tokenStr;
            }

            // Lookup the values from the congiguration
            var audience = _configBuilder.EntraAudience;
            var clientId = _configBuilder.EntraClientID;
            var clientSecret = _configBuilder.EntraSecret;
            var tenantId = _configBuilder.EntraTenantID;

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var tokenRequestContext = new TokenRequestContext(new[] { audience });
            token = credential.GetToken(tokenRequestContext);

            return token?.Token;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Start the server
            Console.WriteLine("Server started");

            var test_endpoint = _configBuilder.TestEndpoint;
            var concurrency = _configBuilder.Concurrency;
            var duration = ParseTime(_configBuilder.DurationSeconds);
            var delay = ParseTime(_configBuilder.InterrunDelay);

            var endTime = DateTime.Now.AddMilliseconds(duration);

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < concurrency; i++)
            {
                // Start a new task
                tasks.Add (Task.Run(() => RunTest(cancellationToken, test_endpoint, delay, endTime)));
            }

            await Task.WhenAll(tasks);

            Console.WriteLine("Server stopped");
        }

        private Dictionary<string, byte[]> _dataCache = new Dictionary<string, byte[]>();

        private async Task RunTest(CancellationToken cancellationToken, string test_endpoint, int delay, DateTime endTime)
        {

            while (!cancellationToken.IsCancellationRequested && DateTime.Now < endTime)
            {
                // Perform some work
               try
                {
                    if (!string.IsNullOrEmpty(test_endpoint))
                    {
                        var testConfigs = _configBuilder.Tests;
                        foreach (var test in testConfigs)
                        {
                            //Console.WriteLine($"Running test ... {test.Name} {test.Path} {test.Method} {test.DataFile}");

                            // Create the request
                            var request = new HttpRequestMessage(new HttpMethod(test.Method), test_endpoint + test.Path);
                            bool hasContentType = false;

                            lock (_lock)
                            {
                                _requestCount++;
                                if (test.Headers == null)
                                {
                                    test.Headers = new Dictionary<string, string>();
                                }
                                test.Headers["x-Request-Sequence"] = _requestCount.ToString();
                            }

                            // Add headers to the request
                            if (test.Headers != null)
                            {
                                foreach (var header in test.Headers)
                                {
                                    // Console.WriteLine($"Header: {header.Key} = {header.Value}");
                                    switch (header.Key.ToLower())
                                    {
                                        case "content-type":
                                            if (request.Content == null)
                                            {
                                                request.Content = new ByteArrayContent(new byte[0]); // Initialize content to avoid null reference
                                            }
                                            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(header.Value);
                                            hasContentType = true;
                                            break;
                                        case "accept":
                                            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(header.Value));
                                            break;
                                        case "user-agent":
                                            request.Headers.UserAgent.ParseAdd(header.Value);
                                            break;
                                        case "content-length":
                                            if (request.Content == null)
                                            {
                                                request.Content = new ByteArrayContent(new byte[0]); // Initialize content to avoid null reference
                                            }
                                            request.Content.Headers.ContentLength = long.Parse(header.Value);
                                            break;
                                        case "authorization":

                                            if (string.Equals(header.Value, "<token>", StringComparison.OrdinalIgnoreCase))
                                            {
                                                var token = GetToken();
                                                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                                            }
                                            else
                                            {
                                                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", header.Value);
                                            }
                                            break;
                                        case "cache-control":
                                            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                                            break;
                                        case "if-modified-since":
                                            request.Headers.IfModifiedSince = DateTimeOffset.Parse(header.Value);
                                            break;
                                        case "referer":
                                            request.Headers.Referrer = new Uri(header.Value);
                                            break;
                                        case "host":
                                            request.Headers.Host = header.Value;
                                            break;
                                        default:
                                            request.Headers.Add(header.Key, header.Value);
                                            break;
                                    }
                                }
                            }

                            // Set content for non-GET requests
                            if (!string.IsNullOrEmpty(test.DataFile))
                            {
                                var data = new byte[0];
                                if (!_dataCache.ContainsKey(test.DataFile))
                                {
                                    data = System.IO.File.ReadAllBytes(test.DataFile);
                                    _dataCache[test.DataFile] = data;
                                }
                                else
                                {
                                    data = _dataCache[test.DataFile];
                                }

                                request.Content = new ByteArrayContent(data);
                                if (!hasContentType)
                                {
                                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                                }
                                request.Content.Headers.ContentLength = data.Length;
                            }
                            else if (request.Method == HttpMethod.Get)
                            {
                                // Ensure no content is set for GET requests
                                request.Content = null;
                            }

                            //Console.WriteLine("Sending request ...");

                            try
                            {
                                // Send the request
                                //Console.WriteLine($"Request Method: {request.Method}");
                                //Console.WriteLine($"Request URI: {request.RequestUri}");
                                if (request.Content != null)
                                {
                                    //Console.WriteLine($"Request Content Length: {request.Content.Headers.ContentLength}");
                                }
                                using (var response = await _httpClient.SendAsync(request, cancellationToken))
                                {
                                    // Read the response
                                    var content = await response.Content.ReadAsStringAsync();
                                    Console.WriteLine($"{response.StatusCode} Content-Length: {response.Content.Headers.ContentLength} <: {test.Name} {test.Path} {test.Method} {test.DataFile}");
                                    //Console.WriteLine($"Response status code {response.StatusCode} Content-Length: {response.Content.Headers.ContentLength}");
                                    // Console.WriteLine($"Response from {test_endpoint}: {content}");
                                }
                            }
                            catch (HttpRequestException httpEx)
                            {
                                Console.WriteLine($"HttpRequestException: {httpEx.Message}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Exception: {ex.Message}");
                            }
                            finally
                            {
                                request.Dispose();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception in processing request: {ex.Message}");
                }
                await Task.Delay(delay, cancellationToken);
            }
        }

        private int ParseTime(string time)
        {
            if (time.EndsWith("ms"))
            {
                return int.Parse(time.Replace("ms", ""));
            }
            else if (time.EndsWith("s"))
            {
                return int.Parse(time.Replace("s", "")) * 1000;
            }
            else
            {
                throw new FormatException($"Invalid time format: {time}");
            }
        }
    }
}