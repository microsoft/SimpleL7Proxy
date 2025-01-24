//using Microsoft.AspNetCore.Hosting.Internal;
//using Microsoft.Azure.Amqp.Framing;
using Microsoft.Extensions.Configuration;
using System;
using System.Net;
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

        private DateTimeOffset tokenExpiry = DateTime.MinValue;
        private Azure.Core.AccessToken? token = null;
        private string tokenStr = "";
        private int totalRequests = 0;

        private List<PreparedTest> allTests = new List<PreparedTest>();
        private Dictionary<int, int> testResults = new Dictionary<int, int>();

        public Server(ConfigBuilder configBuilder, HttpClient httpClient) : base()
        {
            _configBuilder = configBuilder;
            //            _httpClient = httpClient;
            _httpClient = CreateHttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;

            // Allow the client to use self signed certs
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            // Read the config
            InitializeServer();
        }

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
            };
            return new HttpClient(handler);
        }

        private void InitializeServer()
        {
            // Use the settings to initialize the server
            Console.WriteLine($"Test endpoint: {_configBuilder.TestEndpoint}");
            Console.WriteLine($"Duration: {_configBuilder.DurationSeconds}");
            Console.WriteLine($"Concurrency: {_configBuilder.Concurrency}");
            Console.WriteLine($"Delay: {_configBuilder.InterrunDelay}");

            // Parse the tests into memory
            prepareTests(_configBuilder.TestEndpoint);

            if (_configBuilder.needsToken())
            {
                Console.WriteLine("Getting token ...");
                try
                {
                    var tokenStr = GetToken();
                    Console.WriteLine($"Token: {tokenStr}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception in getting token: {ex.Message}");
                }
            }
        }

        private string GetToken()
        {
            if (!string.IsNullOrEmpty(tokenStr))
            {
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

            Console.WriteLine($"Getting token for {audience} {clientId} {clientSecret} {tenantId}");

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var tokenRequestContext = new TokenRequestContext(new[] { audience });
            token = credential.GetToken(tokenRequestContext);

            return token?.Token;
        }

        static int[] stats = new int[6];
        static int[] responseStats = new int[600];
        static int receiveTimeout;
        static int sendTimeout;
        static int _testNumber = 0;
        public async Task StartAsync(CancellationToken cancellationToken2)
        {
            // Start the server
            Console.WriteLine("Server started");

            var test_endpoint = _configBuilder.TestEndpoint;
            var concurrency = _configBuilder.Concurrency;
            var duration = ParseTime(_configBuilder.DurationSeconds);
            var delay = ParseTime(_configBuilder.InterrunDelay);

            // reset all the stats
            while (true)
            {

                totalRequests = 0;
                _requestCount = 0;
                testResults.Clear();

                var endTime = DateTime.Now.AddMilliseconds(duration);
                Console.WriteLine($"{DateTime.Now} Endpoint: {test_endpoint}  Concurrency: {concurrency}  EndTime: {endTime}  Delay: {delay}ms");

                var cancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = cancellationTokenSource.Token;
                List<Task> tasks = new List<Task>();

                // reset the test number
                _testNumber = receiveTimeout = sendTimeout = 0;
                // reset the response stats
                for (int i = 0; i < 600; i++)
                {
                    responseStats[i] = 0;
                }

                for (int i = 0; i < concurrency; i++)
                {
                    // Start a new task
                    tasks.Add(Task.Run(() => RunTest(cancellationToken, test_endpoint, delay, endTime)));
                }
                int prevCompletes =0;
                int reqsPerSec=0;
                // Wait for the threads to startup... This can be improved
                await Task.Delay(5000);

                // Do this while there are active threads.  Each thread exits after the duration has expired
                while (stats[0] > 0)
                {
                    var s = "";
                    for (int i = 0; i < 600; i++)
                    {
                        if (responseStats[i] > 0)
                        {
                            s += $"{i}: {responseStats[i]} ";
                        }
                    }
                    
                    var completes = responseStats[200];
                    reqsPerSec = completes - prevCompletes;
                    prevCompletes = completes;
                    
                    Console.WriteLine($"{DateTime.Now} Stats: Test #: {_testNumber}  Reqs/Sec: {reqsPerSec} Active Threads: {stats[0]}  Begin:{stats[1]} Send:{stats[2]} Read: {stats[3]} Dispose:{stats[4]} Sleep:{stats[5]} Rd Tx: {receiveTimeout} Wr Tx: {sendTimeout}  Codes: {s}");
                    await Task.Delay(1000);
                }

                await Task.WhenAll(tasks);

                Console.WriteLine($"Tests Completed {_requestCount} rquests.");

                // Summarize the results
                Console.WriteLine("Results:");
                foreach (var key in testResults.Keys)
                {
                    Console.WriteLine($"{key} : {testResults[key]}");
                }

                // Wait for a key press to cancel
                Console.WriteLine("\n\nPress q to exit, any other key to repeat: ");
                var k = Console.ReadKey();

                if (k.KeyChar == 'q')
                {
                    break;
                }
            }
        }

        private Dictionary<string, byte[]> _dataCache = new Dictionary<string, byte[]>();

        private async Task RunTest(CancellationToken cancellationToken, string test_endpoint, int delay, DateTime endTime)
        {

            Dictionary<int, int> localTestResults = new Dictionary<int, int>();

            // Count that the thread is running
            Interlocked.Increment(ref stats[0]);

            while (!cancellationToken.IsCancellationRequested && DateTime.Now < endTime)
            {

                int currentTestNumber = 0;
                string line = "";
                try
                {
                    if (!string.IsNullOrEmpty(test_endpoint))
                    {
                        Interlocked.Increment(ref stats[1]);
                        //Console.WriteLine("Sending request ...");
                        foreach (var test in allTests)
                        {
                            var m = new HttpRequestMessage(test.Method, test_endpoint + test.Path);
                            CloneHttpRequestMessage(m, test.request);

                            lock (_lock)
                            {
                                currentTestNumber = _requestCount++;
                                line = $"Test #{currentTestNumber} > {test.Name} {test.Path} {test.Method}";
                                m.Headers.Add("x-Request-Sequence", _requestCount.ToString());
                            }
                            Interlocked.Increment(ref _testNumber);

                            //Console.WriteLine(line);

                            try
                            {
                                // Request has been created

                                Interlocked.Increment(ref stats[2]);
                                // Send the request
                                using (var response = await SendRequestAsync(m, cancellationToken, test.timeout).ConfigureAwait(false))
                                {
                                    // Waiting to read response

                                    // Read the response
                                    Interlocked.Increment(ref stats[3]);

                                    // timeout after test.timeout
                                    using (var cts = new CancellationTokenSource(test.timeout.Value ))
                                    {
                                        try {
                                            var content = await Task.Run(() => response?.Content?.ReadAsStringAsync(cts.Token), cts.Token).ConfigureAwait(false) ?? "N/A";
                                        } catch (TaskCanceledException e) {
                                            Interlocked.Increment(ref receiveTimeout);
                                        }
                                    }
                                    
                                    Interlocked.Decrement(ref stats[3]);

                                    int code = (int)response.StatusCode;

                                    Interlocked.Increment(ref responseStats[code]);
                                    // Add the test result
                                    localTestResults[code] = localTestResults.ContainsKey(code) ? localTestResults[code] + 1 : 1;

                                    // Disposing
                                    Interlocked.Increment(ref stats[4]);
                                    response?.Dispose();
                                    Interlocked.Decrement(ref stats[4]);

                                }

                                Interlocked.Decrement(ref stats[2]);
                            }
                            catch (TaskCanceledException e)
                            {
                                Interlocked.Increment(ref sendTimeout);
                                //localTestResults[408] = localTestResults.ContainsKey(408) ? localTestResults[408] + 1 : 1;
                                //Console.WriteLine($"Test #{currentTestNumber} TaskCanceledException: {test.Name} {e.Message}");
                            }
                            catch (HttpRequestException httpEx)
                            {
                                localTestResults[500] = localTestResults.ContainsKey(500) ? localTestResults[500] + 1 : 1;
                                Console.WriteLine($"HttpRequestException: {httpEx.Message}");
                            }
                            catch (Exception ex)
                            {
                                localTestResults[500] = localTestResults.ContainsKey(500) ? localTestResults[500] + 1 : 1;
                                Console.WriteLine($"Test #{currentTestNumber} Exception: {ex.Message}");
                                Console.WriteLine($"Test #{currentTestNumber} {ex.StackTrace}");
                            }
                            finally
                            {
                                m.Dispose();
                            }

                            // Sleeping
                            Interlocked.Increment(ref stats[5]);
                            // sleep delay ms
                            await Task.Delay(delay).ConfigureAwait(false);
                            Interlocked.Decrement(ref stats[5]);

                        }
                        Interlocked.Decrement(ref stats[1]);
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception in processing request: {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                }
            }

            // Thread existing
            Interlocked.Decrement(ref stats[0]);


            lock (_lock)
            {
                foreach (var key in localTestResults.Keys)
                {
                    testResults[key] = testResults.ContainsKey(key) ? testResults[key] + localTestResults[key] : localTestResults[key];
                }
            }
        }

        private void prepareTests(string test_endpoint)
        {
            var testConfigs = _configBuilder.Tests;

            foreach (var test in testConfigs)
            {
                Console.WriteLine($"Preparing test ... {test.Name} {test.Path} {test.Method} {test.DataFile}");

                // Create the request
                var request = new HttpRequestMessage(new HttpMethod(test.Method), test_endpoint + test.Path);

                // Make a copy of the request

                bool hasContentType = false;

                if (test.Headers == null)
                {
                    test.Headers = new Dictionary<string, string>();
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

                var timeout = _configBuilder.parseTimeout(test.Timeout);
                allTests.Add(new PreparedTest(request, timeout, test.Name, test.Method, test.Path));
            }

        }

        private void CloneHttpRequestMessage(HttpRequestMessage clone, HttpRequestMessage request)
        {
            // Copy the content
            if (request.Content != null)
            {
                clone.Content = new ByteArrayContent(request.Content.ReadAsByteArrayAsync().Result);
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            // Copy the headers
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            // Copy the properties
            foreach (var property in request.Properties)
            {
                clone.Properties.Add(property);
            }
        }

        public async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken, TimeSpan? timeout)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (timeout is not null)
            {
                cts.CancelAfter(timeout.Value);
            }

            try
            {
                return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw;
            }

            return null;

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