using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Test;

public sealed partial class PolicyScenarioIntegrationTests
{
    [TestMethod]
    [RegressionTestCase(
        "request-body-read-failure",
        "Client disconnect during request body returns HTTP 400",
        "A client that ends the upload before its declared Content-Length must receive HTTP 400, emit an S7P exception, and never reach the backend.")]
    [TestCategory("Integration")]
    [TestCategory("RequestLifecycle")]
    [Timeout(120_000)]
    public async Task RequestBody_ClientDisconnect_ReturnsBadRequestWithoutBackendCall()
    {
        var timeout = TimeSpan.FromSeconds(45);
        var pythonExecutable = Environment.GetEnvironmentVariable("CIRCUIT_BREAKER_TEST_PYTHON") ?? "python3";
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-client-read-test-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Client-read test artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(2);
        var backendPort = ports[0];
        var proxyPort = ports[1];
        var backendUrl = $"http://127.0.0.1:{backendPort}";
        var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
        LoggedProcess? backend = null;
        LoggedProcess? proxy = null;
        try
        {
            backend = LoggedProcess.Start(
                "client-read backend",
                CreateStreamServerStartInfo(pythonExecutable, streamServerPath, backendPort),
                Path.Combine(artifactRoot, "backend.stdout.log"),
                Path.Combine(artifactRoot, "backend.stderr.log"));
            await WaitUntilReadyAsync(backend, new Uri($"{backendUrl}/health"), timeout);

            var proxyEnvironment = CreateCircuitBreakerProxyEnvironment();
            proxyEnvironment["LogToEvents"] = "backend,proxy,circuitbreaker,exception";
            proxyEnvironment["IterationMode"] = "SinglePass";
            proxy = LoggedProcess.Start(
                "client-read proxy",
                CreateIteratorProxyStartInfo(
                    proxyEnvironment,
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    [backendPort]),
                Path.Combine(artifactRoot, "proxy.stdout.log"),
                Path.Combine(artifactRoot, "proxy.stderr.log"));
            await WaitForActiveBackendsAsync(proxy, eventLogPath, expectedCount: 1, timeout);

            var requestPath = "/client-read-disconnect-" + Guid.NewGuid().ToString("N");
            var partialBody = Encoding.UTF8.GetBytes("{\"message\":\"partial\"}");
            var responseText = await SendTruncatedRequestAsync(
                proxyPort,
                requestPath,
                partialBody,
                partialBody.Length + 32,
                timeout);

            var statusLine = responseText.Split("\r\n", 2, StringSplitOptions.None)[0];
            StringAssert.StartsWith(statusLine, "HTTP/1.1 400");
            StringAssert.Contains(responseText, "Unable to read request body");

            var exceptionEvent = await WaitForClientReadExceptionEventAsync(
                eventLogPath,
                requestPath,
                timeout);
            Assert.AreEqual("S7P-Exception", GetProperty(exceptionEvent, "Type"));
            Assert.AreEqual("400", GetProperty(exceptionEvent, "Status"));
            Assert.AreEqual("Client Read Exception", GetProperty(exceptionEvent, "Error"));

            using var backendClient = new HttpClient
            {
                BaseAddress = new Uri(backendUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var statsResponse = await backendClient.GetAsync("/stress-stats");
            statsResponse.EnsureSuccessStatusCode();
            using var stats = JsonDocument.Parse(await statsResponse.Content.ReadAsStringAsync());
            Assert.IsFalse(
                stats.RootElement.TryGetProperty(requestPath, out _),
                "The incomplete request was forwarded to the backend.");

        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
            if (backend is not null)
            {
                await backend.DisposeAsync();
            }

            AttachScenarioArtifacts(artifactRoot);
        }
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-retry-after",
        "Retry-After requeues an open backend",
        "A 503 Retry-After response must open the host circuit breaker, delay the queued request, and retry the backend after the configured interval.")]
    [TestCategory("Integration")]
    [TestCategory("CircuitBreaker")]
    [Timeout(120_000)]
    public async Task CircuitBreaker_RetryAfter_RequeuesThenRetriesBackend()
    {
        const int retryAfterMs = 1500;
        var expectedDelayMs = retryAfterMs + Constants.RetryAfterJitterMaxMs;
        var timeout = TimeSpan.FromSeconds(45);
        var pythonExecutable = Environment.GetEnvironmentVariable("CIRCUIT_BREAKER_TEST_PYTHON") ?? "python3";

        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-circuit-breaker-test-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Circuit-breaker test artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(2);
        var backendPort = ports[0];
        var proxyPort = ports[1];
        var backendUrl = $"http://127.0.0.1:{backendPort}";
        var proxyStdoutPath = Path.Combine(artifactRoot, "proxy.stdout.log");
        var backendStdoutPath = Path.Combine(artifactRoot, "backend.stdout.log");
        LoggedProcess? backend = null;
        LoggedProcess? proxy = null;
        try
        {
            backend = LoggedProcess.Start(
                "circuit-breaker backend",
                CreateStreamServerStartInfo(pythonExecutable, streamServerPath, backendPort),
                backendStdoutPath,
                Path.Combine(artifactRoot, "backend.stderr.log"));
            await WaitUntilReadyAsync(backend, new Uri($"{backendUrl}/health"), timeout);

            var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
            var proxyEnvironment = CreateCircuitBreakerProxyEnvironment();
            proxy = LoggedProcess.Start(
                "circuit-breaker proxy",
                CreateIteratorProxyStartInfo(
                    proxyEnvironment,
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    [backendPort]),
                proxyStdoutPath,
                Path.Combine(artifactRoot, "proxy.stderr.log"));

            await WaitForActiveBackendsAsync(proxy, eventLogPath, expectedCount: 1, timeout);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            using (var noDelayResponse = await client.GetAsync("/success"))
            {
                Assert.AreEqual(HttpStatusCode.OK, noDelayResponse.StatusCode);
                Assert.IsFalse(
                    noDelayResponse.Headers.Contains("Request-Requeue-Delay"),
                    "A response with no completed requeue delay must not include Request-Requeue-Delay.");
            }

            var requestKey = Guid.NewGuid().ToString("N");
            var stopwatch = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                $"/retry-after-once?key={requestKey}&retryAfterMs={retryAfterMs}");
            var responseBody = await response.Content.ReadAsStringAsync();
            stopwatch.Stop();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("Retry succeeded", responseBody);

            var proxyLog = await WaitForCircuitBreakerLogAsync(
                proxyStdoutPath,
                "Requeued request",
                timeout);
            var backendLog = await WaitForCircuitBreakerLogAsync(
                backendStdoutPath,
                $"RETRY_AFTER_ONCE key={requestKey} attempt=2",
                timeout);

            var requeueMatch = Regex.Match(
                proxyLog,
                @"All 1 matching backend circuit breakers are open; requeueing after (?<delay>\d+)ms");
            Assert.IsTrue(requeueMatch.Success, "The proxy did not log the all-open requeue decision.");
            var requeueDelayMs = int.Parse(requeueMatch.Groups["delay"].Value, CultureInfo.InvariantCulture);
            Assert.IsTrue(requeueDelayMs > 0 && requeueDelayMs <= expectedDelayMs);

            var delayStartMatch = Regex.Match(
                proxyLog,
                @"Starting requeue delay of (?<delay>\d+)ms");
            Assert.IsTrue(delayStartMatch.Success, "The requeue worker did not log the queued delay.");
            Assert.AreEqual(
                requeueDelayMs,
                int.Parse(delayStartMatch.Groups["delay"].Value, CultureInfo.InvariantCulture));

            var requeuedMatch = Regex.Match(
                proxyLog,
                @"Requeued request,.*DelayMs: (?<delay>\d+)");
            Assert.IsTrue(requeuedMatch.Success, "The requeue worker did not log queue reinsertion.");
            Assert.AreEqual(
                requeueDelayMs,
                int.Parse(requeuedMatch.Groups["delay"].Value, CultureInfo.InvariantCulture));

            Assert.IsTrue(
                response.Headers.TryGetValues("Request-Requeue-Delay", out var singleDelayHeaderValues),
                "The successful response did not include Request-Requeue-Delay.");
            var reportedSingleDelayMs = double.Parse(singleDelayHeaderValues.Single(), CultureInfo.InvariantCulture);
            Assert.IsTrue(
                reportedSingleDelayMs >= requeueDelayMs - 100,
                $"Reported requeue delay was {reportedSingleDelayMs:F3}ms; scheduled delay was {requeueDelayMs}ms.");

            var attemptMatches = Regex.Matches(
                proxyLog,
                @"(?m)^(?<timestamp>\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) " +
                @"\[ProxyToBackEnd:(?<guid>[^\]]+)\] Attempting backend host: .+ " +
                @"\(Attempt #(?<attempt>\d+)\)");
            var firstAttempt = attemptMatches.Cast<Match>()
                .FirstOrDefault(match => match.Groups["attempt"].Value == "1");
            var secondAttempt = attemptMatches.Cast<Match>()
                .FirstOrDefault(match => match.Groups["attempt"].Value == "2");
            Assert.IsNotNull(firstAttempt, "The first proxy backend attempt was not logged.");
            Assert.IsNotNull(secondAttempt, "The retried proxy backend attempt was not logged.");
            Assert.AreEqual(firstAttempt.Groups["guid"].Value, secondAttempt.Groups["guid"].Value);

            var firstProxyAttempt = ParseCircuitBreakerLogTimestamp(firstAttempt.Groups["timestamp"].Value);
            var secondProxyAttempt = ParseCircuitBreakerLogTimestamp(secondAttempt.Groups["timestamp"].Value);
            if (secondProxyAttempt < firstProxyAttempt)
            {
                secondProxyAttempt = secondProxyAttempt.AddYears(1);
            }
            var proxyAttemptDelayMs = (secondProxyAttempt - firstProxyAttempt).TotalMilliseconds;

            var backendAttempts = Regex.Matches(
                backendLog,
                $@"RETRY_AFTER_ONCE key={Regex.Escape(requestKey)} attempt=(?<attempt>[12]) " +
                @"timestamp=(?<timestamp>\d+\.\d+) retry_after_ms=1500");
            Assert.AreEqual(2, backendAttempts.Count, "The null server must receive exactly two keyed attempts.");
            var firstBackendTimestamp = ParseBackendTimestamp(backendAttempts, "1");
            var secondBackendTimestamp = ParseBackendTimestamp(backendAttempts, "2");
            var backendAttemptDelayMs = (secondBackendTimestamp - firstBackendTimestamp) * 1000;

            Assert.IsTrue(
                proxyAttemptDelayMs >= expectedDelayMs - 100,
                $"Proxy retry occurred after {proxyAttemptDelayMs:F0}ms; expected at least {expectedDelayMs - 100}ms.");
            Assert.IsTrue(
                backendAttemptDelayMs >= expectedDelayMs - 100,
                $"Backend retry occurred after {backendAttemptDelayMs:F0}ms; expected at least {expectedDelayMs - 100}ms.");
            Assert.IsTrue(
                stopwatch.Elapsed.TotalMilliseconds >= expectedDelayMs - 100,
                $"Client completed after {stopwatch.Elapsed.TotalMilliseconds:F0}ms; expected a queued retry delay.");

            var proxyLogBeforeRepeatedRequeues = await File.ReadAllTextAsync(proxyStdoutPath);
            var repeatedRequeueStopwatch = Stopwatch.StartNew();
            using var repeatedRequeueResponse = await client.GetAsync("/429error?retryAfterMs=50");
            var repeatedRequeueBody = await repeatedRequeueResponse.Content.ReadAsStringAsync();
            repeatedRequeueStopwatch.Stop();

            Assert.AreEqual(HttpStatusCode.PreconditionFailed, repeatedRequeueResponse.StatusCode);
            StringAssert.Contains(repeatedRequeueBody, "Maximum backend attempts reached (5).");
            Assert.IsTrue(
                repeatedRequeueResponse.Headers.TryGetValues("Request-Requeue-Delay", out var cumulativeDelayHeaderValues),
                "The terminal response did not include Request-Requeue-Delay.");

            var proxyLogAfterRepeatedRequeues = await File.ReadAllTextAsync(proxyStdoutPath);
            var repeatedRequeueLog = proxyLogAfterRepeatedRequeues[proxyLogBeforeRepeatedRequeues.Length..];
            var repeatedDelayMatches = Regex.Matches(
                repeatedRequeueLog,
                @"Starting requeue delay of (?<delay>\d+)ms");
            Assert.IsTrue(
                repeatedDelayMatches.Count >= 2,
                $"Expected at least two completed requeue delays, but found {repeatedDelayMatches.Count}.");

            var scheduledCumulativeDelayMs = repeatedDelayMatches
                .Select(match => int.Parse(match.Groups["delay"].Value, CultureInfo.InvariantCulture))
                .Sum();
            var reportedCumulativeDelayMs = double.Parse(cumulativeDelayHeaderValues.Single(), CultureInfo.InvariantCulture);
            Assert.IsTrue(
                reportedCumulativeDelayMs >= scheduledCumulativeDelayMs - 100,
                $"Reported cumulative delay was {reportedCumulativeDelayMs:F3}ms; " +
                $"scheduled delays totaled {scheduledCumulativeDelayMs}ms.");
            Assert.IsTrue(
                reportedCumulativeDelayMs <= repeatedRequeueStopwatch.Elapsed.TotalMilliseconds,
                $"Reported cumulative delay {reportedCumulativeDelayMs:F3}ms exceeded total client latency " +
                $"{repeatedRequeueStopwatch.Elapsed.TotalMilliseconds:F3}ms.");

            TestContext.WriteLine(
                $"Retry-After={retryAfterMs}ms, jitter={Constants.RetryAfterJitterMaxMs}ms, " +
                $"queued delay={requeueDelayMs}ms, proxy attempt interval={proxyAttemptDelayMs:F0}ms, " +
                $"backend attempt interval={backendAttemptDelayMs:F0}ms, " +
                $"cumulative requeue delay={reportedCumulativeDelayMs:F3}ms across {repeatedDelayMatches.Count} delays.");
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }

            if (backend is not null)
            {
                await backend.DisposeAsync();
            }

            AttachScenarioArtifacts(artifactRoot);
        }
    }

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-multi-host-selection",
        "Throttled hosts are skipped while peers remain available",
        "A backend with an active Retry-After deadline must fail over to an available peer and must not receive subsequent requests until its deadline expires.")]
    [TestCategory("Integration")]
    [TestCategory("CircuitBreaker")]
    [Timeout(120_000)]
    public async Task CircuitBreaker_MultipleHosts_SelectsAvailableBackend()
    {
        const int retryAfterMs = 5000;
        var timeout = TimeSpan.FromSeconds(45);
        var pythonExecutable = Environment.GetEnvironmentVariable("CIRCUIT_BREAKER_TEST_PYTHON") ?? "python3";
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-circuit-breaker-multi-host-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Multi-host circuit-breaker artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(3);
        var availablePort = ports[0];
        var throttledPort = ports[1];
        var proxyPort = ports[2];
        var availableUrl = $"http://127.0.0.1:{availablePort}";
        var throttledUrl = $"http://127.0.0.1:{throttledPort}";
        var availableLogPath = Path.Combine(artifactRoot, "available-backend.stdout.log");
        var throttledLogPath = Path.Combine(artifactRoot, "throttled-backend.stdout.log");
        var proxyLogPath = Path.Combine(artifactRoot, "proxy.stdout.log");
        var backends = new List<LoggedProcess>();
        LoggedProcess? proxy = null;
        try
        {
            backends.Add(LoggedProcess.Start(
                "available backend",
                CreateStreamServerStartInfo(pythonExecutable, streamServerPath, availablePort),
                availableLogPath,
                Path.Combine(artifactRoot, "available-backend.stderr.log")));
            backends.Add(LoggedProcess.Start(
                "throttled backend",
                CreateStreamServerStartInfo(pythonExecutable, streamServerPath, throttledPort),
                throttledLogPath,
                Path.Combine(artifactRoot, "throttled-backend.stderr.log")));

            await Task.WhenAll(
                WaitUntilReadyAsync(backends[0], new Uri($"{availableUrl}/health"), timeout),
                WaitUntilReadyAsync(backends[1], new Uri($"{throttledUrl}/health"), timeout));

            var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
            proxy = LoggedProcess.Start(
                "multi-host circuit-breaker proxy",
                CreateIteratorProxyStartInfo(
                    CreateMultiHostCircuitBreakerProxyEnvironment(),
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    [availablePort, throttledPort]),
                proxyLogPath,
                Path.Combine(artifactRoot, "proxy.stderr.log"));
            await WaitForActiveBackendsAsync(proxy, eventLogPath, expectedCount: 2, timeout);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            var firstKey = Guid.NewGuid().ToString("N");
            var secondKey = Guid.NewGuid().ToString("N");
            var firstPath =
                $"/retry-after-once?key={firstKey}&retryAfterMs={retryAfterMs}&throttlePort={throttledPort}";
            var secondPath =
                $"/retry-after-once?key={secondKey}&retryAfterMs={retryAfterMs}&throttlePort={throttledPort}";

            using var firstResponse = await client.GetAsync(firstPath);
            Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.AreEqual("Retry succeeded", await firstResponse.Content.ReadAsStringAsync());

            using var secondResponse = await client.GetAsync(secondPath);
            Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);
            Assert.AreEqual("Retry succeeded", await secondResponse.Content.ReadAsStringAsync());

            var throttledLog = await WaitForCircuitBreakerLogAsync(
                throttledLogPath,
                $"RETRY_AFTER_ONCE key={firstKey}",
                timeout);
            var availableLog = await WaitForCircuitBreakerLogAsync(
                availableLogPath,
                $"RETRY_AFTER_ONCE key={secondKey}",
                timeout);
            var proxyLog = await WaitForCircuitBreakerLogAsync(proxyLogPath, secondPath, timeout);

            StringAssert.Contains(throttledLog, $"RETRY_AFTER_ONCE key={firstKey}");
            StringAssert.Contains(throttledLog, "throttled=true");
            Assert.IsFalse(
                throttledLog.Contains(secondKey, StringComparison.Ordinal),
                "The backend with an active Retry-After deadline received the second request.");
            StringAssert.Contains(availableLog, $"RETRY_AFTER_ONCE key={firstKey}");
            StringAssert.Contains(availableLog, $"RETRY_AFTER_ONCE key={secondKey}");

            var firstStart = proxyLog.IndexOf(firstPath, StringComparison.Ordinal);
            var secondStart = proxyLog.IndexOf(secondPath, StringComparison.Ordinal);
            Assert.IsTrue(firstStart >= 0 && secondStart > firstStart, "Proxy request log sections were not ordered as expected.");
            var firstSection = proxyLog[firstStart..secondStart];
            var secondSection = proxyLog[secondStart..];

            var firstThrottleAttempt = firstSection.IndexOf(
                $"Attempting backend host: {throttledUrl} (Attempt #1)",
                StringComparison.Ordinal);
            var firstThrottleResponse = firstSection.IndexOf(
                $"Received response from {throttledUrl} - Status: ServiceUnavailable",
                StringComparison.Ordinal);
            var firstAvailableAttempt = firstSection.IndexOf(
                $"Attempting backend host: {availableUrl} (Attempt #2)",
                StringComparison.Ordinal);
            var firstAvailableResponse = firstSection.IndexOf(
                $"Received response from {availableUrl} - Status: OK",
                StringComparison.Ordinal);
            Assert.IsTrue(
                firstThrottleAttempt >= 0 &&
                firstThrottleResponse > firstThrottleAttempt &&
                firstAvailableAttempt > firstThrottleResponse &&
                firstAvailableResponse > firstAvailableAttempt,
                "The first request did not fail over from the throttled backend to the available backend.");

            Assert.IsFalse(
                secondSection.Contains($"Attempting backend host: {throttledUrl}", StringComparison.Ordinal),
                "The proxy attempted the still-throttled backend on the second request.");
            StringAssert.Contains(
                secondSection,
                $"Attempting backend host: {availableUrl} (Attempt #1)");
            StringAssert.Contains(
                secondSection,
                $"Received response from {availableUrl} - Status: OK");
            Assert.IsFalse(
                proxyLog.Contains("matching backend circuit breakers are open; requeueing", StringComparison.Ordinal),
                "The proxy requeued even though an available backend existed.");

            TestContext.WriteLine(
                $"First request: {throttledUrl} 503 -> {availableUrl} 200. " +
                $"Second request: skipped {throttledUrl} -> {availableUrl} 200.");
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }

            foreach (var backend in backends)
            {
                await backend.DisposeAsync();
            }

            AttachScenarioArtifacts(artifactRoot);
        }
    }

    private static Dictionary<string, string> CreateCircuitBreakerProxyEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EVENT_LOGGERS"] = "file",
            ["LogToEvents"] = "backend,proxy,circuitbreaker",
            ["LogToConsole"] = "none",
            ["LogToAI"] = "none",
            ["LOG_LEVEL"] = "Debug",
            ["LOGDATETIME"] = "true",
            ["APPINSIGHTS_CONNECTIONSTRING"] = "",
            ["AsyncModeEnabled"] = "false",
            ["UseProfiles"] = "false",
            ["UserConfigRequired"] = "false",
            ["ValidateAuthAppID"] = "false",
            ["ValidateAuthConfig"] = "enabled=false, mode=none, header=S7P-KEY",
            ["Workers"] = "1",
            ["DefaultPriority"] = "1",
            ["PriorityKeyHeader"] = "S7PPriorityKey",
            ["PriorityKeys"] = "default",
            ["PriorityValues"] = "1",
            ["LoadBalanceMode"] = "roundrobin",
            ["IterationMode"] = "MultiPass",
            ["MaxAttempts"] = "5",
            ["UseSharedIterators"] = "false",
            ["MaxQueueLength"] = "100",
            ["PollInterval"] = "250",
            ["PollTimeout"] = "1000",
            ["Timeout"] = "5000",
            ["DefaultTTLSecs"] = "30",
            ["CBErrorThreshold"] = "2",
            ["CBTimeslice"] = "1",
            ["Host_retry_after"] =
                "host=http://127.0.0.1:{BACKEND_A_PORT}; path=/; processor=DefaultStream; " +
                "probe=/health; enabled=true; retryafter=true"
        };

    private static Dictionary<string, string> CreateMultiHostCircuitBreakerProxyEnvironment() =>
        new(CreateCircuitBreakerProxyEnvironment(), StringComparer.OrdinalIgnoreCase)
        {
            ["Host_retry_after"] = string.Empty,
            ["Host1"] =
                "host=http://127.0.0.1:{BACKEND_A_PORT}; path=/; processor=DefaultStream; " +
                "probe=/health; enabled=true; retryafter=true",
            ["Host2"] =
                "host=http://127.0.0.1:{BACKEND_B_PORT}; path=/; processor=DefaultStream; " +
                "probe=/health; enabled=true; retryafter=true"
        };

    private static async Task<string> WaitForCircuitBreakerLogAsync(
        string path,
        string expectedText,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        string log = string.Empty;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (File.Exists(path))
                {
                    log = await File.ReadAllTextAsync(path);
                    if (log.Contains(expectedText, StringComparison.Ordinal))
                    {
                        return log;
                    }
                }
            }
            catch (IOException)
            {
                // The process logger may be flushing; retry on the next poll.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for '{expectedText}' in {path}." + Environment.NewLine + log);
    }

    private static async Task<string> SendTruncatedRequestAsync(
        int proxyPort,
        string requestPath,
        byte[] partialBody,
        int declaredContentLength,
        TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cancellation = new CancellationTokenSource(timeout);
        await client.ConnectAsync(IPAddress.Loopback, proxyPort, cancellation.Token);
        await using var stream = client.GetStream();
        var headers = Encoding.ASCII.GetBytes(
            $"POST {requestPath} HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{proxyPort}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {declaredContentLength}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellation.Token);
        await stream.WriteAsync(partialBody, cancellation.Token);
        await stream.FlushAsync(cancellation.Token);
        client.Client.Shutdown(SocketShutdown.Send);

        using var response = new MemoryStream();
        await stream.CopyToAsync(response, cancellation.Token);
        return Encoding.ASCII.GetString(response.ToArray());
    }

    private static async Task<JsonElement> WaitForClientReadExceptionEventAsync(
        string eventLogPath,
        string requestPath,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (File.Exists(eventLogPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(eventLogPath))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var root = document.RootElement;
                            if (string.Equals(GetProperty(root, "Type"), "S7P-Exception", StringComparison.Ordinal) &&
                                string.Equals(GetProperty(root, "Path"), requestPath, StringComparison.Ordinal) &&
                                string.Equals(GetProperty(root, "Error"), "Client Read Exception", StringComparison.Ordinal))
                            {
                                return root.Clone();
                            }
                        }
                        catch (JsonException)
                        {
                            // Ignore a line that is still being written.
                        }
                    }
                }
                catch (IOException)
                {
                    // The file logger may be flushing; retry on the next poll.
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for the client-read exception event for {requestPath} in {eventLogPath}.");
    }

    private static DateTime ParseCircuitBreakerLogTimestamp(string value)
        => DateTime.ParseExact(
            $"2000-{value}",
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private static double ParseBackendTimestamp(MatchCollection matches, string attempt)
    {
        var match = matches.Cast<Match>()
            .Single(item => item.Groups["attempt"].Value == attempt);
        return double.Parse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture);
    }
}