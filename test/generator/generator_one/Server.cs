//using Microsoft.AspNetCore.Hosting.Internal;
//using Microsoft.Azure.Amqp.Framing;
using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
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

            // Certificate validation is handled in HttpClientHandler

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

        private string? GetToken()
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



        static int[] stats = new int[8];
        static int[] responseStats = new int[600];
        static int receiveTimeout;
        static int sendTimeout;
        static int _testNumber = 0;
        static int unreadyTasks = 0;
        public async Task<bool> StartAsync(CancellationToken cancellationToken2)
        {
            // Start the server
            Console.WriteLine("Server started");

            // Initialize ThreadState with the stats array
            ThreadState.Initialize(stats);

            // Initialize ThreadState with the stats array
            ThreadState.Initialize(stats);

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
                Console.WriteLine("Press SPACEBAR to interrupt the current run");

                var cancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = cancellationTokenSource.Token;
                List<Task> tasks = new List<Task>();

                // Start a background task to monitor for spacebar press
                var keyMonitorTask = Task.Run(() => MonitorKeyPress(cancellationTokenSource));

                // reset the test number and stats
                _testNumber = receiveTimeout = sendTimeout = 0;
                unreadyTasks = concurrency;
                for (int i = 0; i < stats.Length; i++)
                {
                    stats[i] = 0;
                }
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
                int[] responseStatsCopy = new int[600];
                int[] oldResponseStats = new int[600];

                // Wait for the tests to start
                await WaitForStartup();

                // Do this while there are active threads.  Each thread exits after the duration has expired
                while (stats[0] > 0 && !cancellationToken.IsCancellationRequested)
                {
                    // Make a snapshot of the response stats    
                    Array.Copy(responseStats, responseStatsCopy, 600);

                    var statusCodes = "";
                    for (int i = 0; i < 600; i++)
                    {
                        if (responseStatsCopy[i] > 0)
                        {
                            statusCodes += $"{i}-{responseStatsCopy[i] - oldResponseStats[i]}, ";
                        }
                    }
                    statusCodes = statusCodes.TrimEnd(',', ' ');
    
                    // Ensure the statusCodes string is at least 40 characters long
                    if (statusCodes.Length < 40)
                    {
                        statusCodes = statusCodes.PadRight(40);
                    }

                    var completes = responseStats[200];
                    reqsPerSec = completes - prevCompletes;
                    prevCompletes = completes;

                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} #{_testNumber}- {reqsPerSec} Reqs/Sec  Status: {statusCodes}  Timeouts: R-{receiveTimeout}, W-{sendTimeout}  Conns: {stats[0]} [{ThreadState.GetStateString()}]");    

                    Array.Copy(responseStatsCopy, oldResponseStats, 600);
                    await Task.Delay(1000);
                }

                // Cancel the operation if spacebar was pressed
                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("\nRun interrupted by user (SPACEBAR pressed)");
                }

                await Task.WhenAll(tasks);

                Console.WriteLine($"Tests Completed {_requestCount} rquests.");

                // Summarize the results
                Console.WriteLine("Results:");
                for (int i = 0; i < 600; i++)
                {
                    if (responseStats[i] > 0)
                    {
                        Console.WriteLine($"{i} : {responseStats[i]}");
                    }
                }
                var successP = responseStats[200] / _testNumber;
                reqsPerSec = _testNumber / duration;
                var latency = latencies.Count > 0 ? latencies.Average() : 0;

                // if duration is in the seconds, then display it in seconds rather than ms
                var durationStr = duration < 1000 ? $"{duration} ms" : $"{duration / 1000} s";
                
                // trim the latency to seconds and 3 decimal places
                var latencyFlt = (float)Math.Round(latency / 1000, 3);

                Console.WriteLine($"{DateTime.Now:HH:mm:ss} Total Requests: {totalRequests}  Success % {successP}  {reqsPerSec} Reqs/Sec   Avg Latency: {latencyFlt} ms Total Time: {durationStr}");

                // Wait for a key press to cancel
                Console.WriteLine("\n\nPress 'r' to repeat, 'c' to reload config, 'q' to quit: ");
                var k = Console.ReadKey();

                if (k.KeyChar == 'q' || k.KeyChar == 'Q')
                {
                    return false;
                }
                else if (k.KeyChar == 'c' || k.KeyChar == 'C')
                {
                    Console.WriteLine("\nReloading configuration...\n");
                    return true;
                }
                else if (k.KeyChar == 'r' || k.KeyChar == 'R')
                {
                    Console.WriteLine("\nRepeating test...\n");
                }
            }
        }

        private void MonitorKeyPress(CancellationTokenSource cancellationTokenSource)
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Spacebar)
                    {
                        cancellationTokenSource.Cancel();
                        break;
                    }
                }
                Thread.Sleep(100); // Check every 100ms to avoid high CPU usage
            }
        }

        TaskCompletionSource testsStartup = new TaskCompletionSource();
        List<float> latencies = new List<float>();

        public Task WaitForStartup()
        {
            testsStartup = new TaskCompletionSource();
            
            return testsStartup.Task;
        }

        public void SignalStart() {
            testsStartup.SetResult();
        }

        private Dictionary<string, byte[]> _dataCache = new Dictionary<string, byte[]>();

        private async Task RunTest(CancellationToken cancellationToken, string test_endpoint, int delay, DateTime endTime)
        {
            ThreadState threadState = new ThreadState();
            
            // Increment active count
            Interlocked.Increment(ref stats[0]);

            // Decrement unready tasks and signal if this is the last one
            int remaining = Interlocked.Decrement(ref unreadyTasks);
            if (remaining == 0)
            {
                latencies.Clear();
                SignalStart();
            }

            // Wait until all threads are ready (unreadyTasks reaches 0)
            while (unreadyTasks > 0)
            {
                await Task.Delay(10);
            }

            // Now transition to Begin state to start processing
            threadState.ChangeState(TestThreadState.Begin);

            Stopwatch sw = new Stopwatch();

            while (!cancellationToken.IsCancellationRequested && DateTime.Now < endTime)
            {

                int currentTestNumber = 0;
                string line = "";
                try
                {
                    if (!string.IsNullOrEmpty(test_endpoint))
                    {
                        threadState.ChangeState(TestThreadState.Begin);
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

                                threadState.ChangeState(TestThreadState.Sending);
                                sw.Restart();
                                // Send the request
                                using (var response = await SendRequestAsync(m, cancellationToken, test.timeout).ConfigureAwait(false))
                                {
                                    // Waiting to read response

                                    // Read the response
                                    threadState.ChangeState(TestThreadState.Reading);

                                    // Check if response is a text/event-stream
                                    var contentType = response?.Content?.Headers?.ContentType?.MediaType;
                                    bool isEventStream = contentType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

                                    // timeout after test.timeout
                                    if (test.timeout.HasValue)
                                    {
                                        using (var cts = new CancellationTokenSource(test.timeout.Value))
                                        {
                                            try
                                            {
                                                if (isEventStream && response?.Content != null)
                                                {
                                                    // Read stream line by line for SSE
                                                    using (var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
                                                    using (var reader = new System.IO.StreamReader(stream))
                                                    {
                                                        while (!reader.EndOfStream)
                                                        {
                                                            var streamLine = await reader.ReadLineAsync().ConfigureAwait(false);
                                                            // Process SSE line (could log or count events here)
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    var content = await Task.Run(() => response?.Content?.ReadAsStringAsync(cts.Token), cts.Token).ConfigureAwait(false) ?? "N/A";
                                                }
                                            }
                                            catch (TaskCanceledException)
                                            {
                                                Interlocked.Increment(ref receiveTimeout);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        try
                                        {
                                            if (isEventStream && response?.Content != null)
                                            {
                                                // Read stream line by line for SSE
                                                using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                                                using (var reader = new System.IO.StreamReader(stream))
                                                {
                                                    while (!reader.EndOfStream)
                                                    {
                                                        var streamLine = await reader.ReadLineAsync().ConfigureAwait(false);
                                                        // Process SSE line (could log or count events here)
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                var content = await Task.Run(() => response?.Content?.ReadAsStringAsync(), cancellationToken).ConfigureAwait(false) ?? "N/A";
                                            }
                                        }
                                        catch (TaskCanceledException)
                                        {
                                            Interlocked.Increment(ref receiveTimeout);
                                        }
                                    }

                                    threadState.ChangeState(TestThreadState.Calculate);

                                    int code = (int)response.StatusCode;
                                    code = code < 600 ? code : 599;

                                    // testing... make the code some random number:  one of:  400,408,412,417,429,500,502,503,504,599
                                   // code = new int[] { 400, 408, 412, 417, 429, 500, 502, 503, 504, 599 }[new Random().Next(0, 10)];

                                    // Increment the response stats for the code
                                    Interlocked.Increment(ref responseStats[code]);
                                    // Add the test result

                                    // Disposing
                                    threadState.ChangeState(TestThreadState.Disposing);
                                    response?.Dispose();

                                }
                                sw.Stop();
                                latencies.Add(sw.ElapsedMilliseconds);

                            }
                            catch (TaskCanceledException)
                            {
                                Interlocked.Increment(ref sendTimeout);
                                //Console.WriteLine($"Test #{currentTestNumber} TaskCanceledException: {test.Name} {e.Message}");
                            }
                            catch (HttpRequestException httpEx)
                            {
                                Interlocked.Increment(ref responseStats[500]);
                                Console.WriteLine($"HttpRequestException: {httpEx.Message}");
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref responseStats[500]);
                                Console.WriteLine($"Test #{currentTestNumber} Exception: {ex.Message}");
                                Console.WriteLine($"Test #{currentTestNumber} {ex.StackTrace}");
                            }
                            finally
                            {
                                m.Dispose();
                            }

                            // Sleeping
                            threadState.ChangeState(TestThreadState.Sleeping);
                            // sleep delay ms
                            await Task.Delay(delay).ConfigureAwait(false);

                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception in processing request: {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                }
            }

            // Thread exiting - decrement active count and reset state
            Interlocked.Decrement(ref stats[0]);
            threadState.Reset();
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

            // Copy the options
            foreach (var option in request.Options)
            {
                var key = new HttpRequestOptionsKey<object>(option.Key);
                clone.Options.Set(key, option.Value);
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
            catch (Exception)
            {
                throw;
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