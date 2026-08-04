using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace SimpleL7Proxy.Test;

public sealed partial class PolicyScenarioIntegrationTests
{
    private const int CircuitBreakerStressRequestors = 48;
    private static readonly TimeSpan s_circuitBreakerStressDuration = TimeSpan.FromSeconds(60);

    [TestMethod]
    [RegressionTestCase(
        "circuit-breaker-three-host-stress",
        "Three backends complete mixed throttling traffic",
        "Runs sustained success, Retry-After, TTL, max-attempt, and terminal-429 traffic through three null servers and requires every started request to complete with its designed status.")]
    [TestCategory("Integration")]
    [TestCategory("CircuitBreaker")]
    [TestCategory("Stress")]
    [TestCategory("Load")]
    [Timeout(240_000)]
    public async Task CircuitBreaker_ThreeHosts_CompletesSixtySecondsOfMixedTraffic()
    {
        var pythonExecutable = Environment.GetEnvironmentVariable("CIRCUIT_BREAKER_TEST_PYTHON") ?? "python3";
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var runId = $"cb-stress-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"simplel7proxy-{runId}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Circuit-breaker stress artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(4);
        var backendPorts = ports.Take(3).ToArray();
        var proxyPort = ports[3];
        var backendUrls = backendPorts.Select(port => $"http://127.0.0.1:{port}").ToArray();
        var backends = new List<LoggedProcess>();
        LoggedProcess? proxy = null;
        try
        {
            for (int backendIndex = 0; backendIndex < backendPorts.Length; backendIndex++)
            {
                var startInfo = CreateStreamServerStartInfo(
                    pythonExecutable,
                    streamServerPath,
                    backendPorts[backendIndex]);
                startInfo.Environment["NULL_SERVER_QUIET"] = "true";
                backends.Add(LoggedProcess.Start(
                    $"stress backend {(char)('A' + backendIndex)}",
                    startInfo,
                    Path.Combine(artifactRoot, $"backend-{backendIndex + 1}.stdout.log"),
                    Path.Combine(artifactRoot, $"backend-{backendIndex + 1}.stderr.log")));
            }

            await Task.WhenAll(backends.Select((backend, index) =>
                WaitUntilReadyAsync(backend, new Uri($"{backendUrls[index]}/health"), TimeSpan.FromSeconds(45))));

            var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
            proxy = LoggedProcess.Start(
                "three-host circuit-breaker stress proxy",
                CreateIteratorProxyStartInfo(
                    CreateCircuitBreakerStressProxyEnvironment(),
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    backendPorts),
                Path.Combine(artifactRoot, "proxy.stdout.log"),
                Path.Combine(artifactRoot, "proxy.stderr.log"));
            await WaitForStressProxyHostsAsync(proxy, proxyPort, expectedHostCount: 3, TimeSpan.FromSeconds(45));

            using var loadHandler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = CircuitBreakerStressRequestors,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            using var loadClient = new HttpClient(loadHandler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
                Timeout = Timeout.InfiniteTimeSpan
            };

            var metrics = new CircuitBreakerStressMetrics();
            var startedAtUtc = DateTime.UtcNow;
            var deadlineUtc = startedAtUtc + s_circuitBreakerStressDuration;
            var stopwatch = Stopwatch.StartNew();
            var requestors = Enumerable.Range(0, CircuitBreakerStressRequestors)
                .Select(requestorId => RunCircuitBreakerStressRequestorAsync(
                    requestorId,
                    GetCircuitBreakerStressScenario(requestorId),
                    runId,
                    deadlineUtc,
                    loadClient,
                    metrics))
                .ToArray();
            await Task.WhenAll(requestors);
            stopwatch.Stop();

            using var controlClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var backendStats = new Dictionary<string, int>[backendUrls.Length];
            for (int backendIndex = 0; backendIndex < backendUrls.Length; backendIndex++)
            {
                backendStats[backendIndex] = await controlClient.GetFromJsonAsync<Dictionary<string, int>>(
                    $"{backendUrls[backendIndex]}/stress-stats") ?? [];
            }

            var report = FormatCircuitBreakerStressReport(
                stopwatch.Elapsed,
                metrics,
                backendUrls,
                backendStats);
            var reportPath = Path.Combine(artifactRoot, "stress-summary.txt");
            await File.WriteAllTextAsync(reportPath, report, new UTF8Encoding(false));
            TestContext.WriteLine(report);

            Assert.IsTrue(
                stopwatch.Elapsed >= s_circuitBreakerStressDuration,
                $"Load phase ended early after {stopwatch.Elapsed}.");
            Assert.IsTrue(metrics.Started >= 2_000, $"Expected thousands of requests; started {metrics.Started:N0}.");
            Assert.AreEqual(metrics.Started, metrics.Completed, "Every started request must complete.");
            Assert.AreEqual(0L, metrics.InFlight, "No requests may remain in flight after draining.");
            Assert.AreEqual(0L, metrics.Unexpected, "Every request must complete with its scenario's designed status.");

            foreach (var scenario in Enum.GetValues<CircuitBreakerStressScenario>())
            {
                Assert.IsTrue(metrics.StartedFor(scenario) > 0, $"No {scenario} requests were started.");
                Assert.AreEqual(
                    metrics.StartedFor(scenario),
                    metrics.CompletedFor(scenario),
                    $"Not every {scenario} request completed.");
                Assert.IsTrue(
                    metrics.OutcomeCount(scenario, ExpectedCircuitBreakerStressStatus(scenario)) > 0,
                    $"The {scenario} scenario did not produce its designed status.");
            }

            for (int backendIndex = 0; backendIndex < 2; backendIndex++)
            {
                Assert.IsTrue(
                    backendStats[backendIndex].GetValueOrDefault("/success") > 0,
                    $"Retry-aware backend {backendUrls[backendIndex]} did not receive success/TTL traffic.");
                Assert.IsTrue(
                    backendStats[backendIndex].GetValueOrDefault("/500error") > 0,
                    $"Retry-aware backend {backendUrls[backendIndex]} did not receive max-attempt traffic.");
                Assert.AreEqual(
                    0,
                    backendStats[backendIndex].GetValueOrDefault("/429terminal"),
                    $"Retry-aware backend {backendUrls[backendIndex]} received no-retry traffic.");
            }

            Assert.IsTrue(
                backendStats[2].GetValueOrDefault("/429terminal") > 0,
                "The retryafter=false backend did not receive terminal-429 traffic.");
            Assert.AreEqual(0, backendStats[2].GetValueOrDefault("/success"));
            Assert.AreEqual(0, backendStats[2].GetValueOrDefault("/500error"));
            Assert.AreEqual(0, backendStats[2].GetValueOrDefault("/retry-after-once"));

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

    private static async Task RunCircuitBreakerStressRequestorAsync(
        int requestorId,
        CircuitBreakerStressScenario scenario,
        string runId,
        DateTime deadlineUtc,
        HttpClient client,
        CircuitBreakerStressMetrics metrics)
    {
        long sequence = 0;
        while (DateTime.UtcNow < deadlineUtc)
        {
            sequence++;
            metrics.Begin(scenario);
            var outcome = "unknown";
            var detail = string.Empty;

            try
            {
                using var request = CreateCircuitBreakerStressRequest(
                    requestorId,
                    sequence,
                    scenario,
                    runId);
                using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);
                outcome = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                if ((int)response.StatusCode == ExpectedCircuitBreakerStressStatus(scenario))
                {
                    await response.Content.CopyToAsync(Stream.Null, requestTimeout.Token);
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                    detail = $"requestor={requestorId} sequence={sequence} " +
                        $"attempts={ReadStressHeader(response, "Attempts")} " +
                        $"lifetimeAttempts={ReadStressHeader(response, "Lifetime-Attempts")} " +
                        $"body={body[..Math.Min(body.Length, 1000)]}";
                }
            }
            catch (OperationCanceledException exception)
            {
                outcome = "timeout";
                detail = exception.Message;
            }
            catch (HttpRequestException exception)
            {
                outcome = "http-" + exception.HttpRequestError;
                detail = exception.Message;
            }
            catch (Exception exception)
            {
                outcome = "exception-" + exception.GetType().Name;
                detail = exception.ToString();
            }
            finally
            {
                metrics.Complete(scenario, outcome, detail);
            }
        }
    }

    private static string ReadStressHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : "missing";

    private static HttpRequestMessage CreateCircuitBreakerStressRequest(
        int requestorId,
        long sequence,
        CircuitBreakerStressScenario scenario,
        string runId)
    {
        var suffix = $"run={runId}&requestor={requestorId}&sequence={sequence}";
        var path = scenario switch
        {
            CircuitBreakerStressScenario.Success => $"/success?{suffix}",
            CircuitBreakerStressScenario.RetryAfter =>
                $"/retry-after-once?key={runId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 5}&retryAfterMs=100&{suffix}",
            CircuitBreakerStressScenario.TtlExpired => $"/success?delay=250ms&{suffix}",
            CircuitBreakerStressScenario.MaxAttempts => $"/500error?{suffix}",
            CircuitBreakerStressScenario.NoRetryAfter => $"/noretry/429terminal?{suffix}",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("S7PPriorityKey", scenario.ToString());
        request.Headers.TryAddWithoutValidation(
            "S7P-Iterator",
            scenario == CircuitBreakerStressScenario.NoRetryAfter ? "SinglePass" : "MultiPass");
        request.Headers.TryAddWithoutValidation(
            "S7PTTL",
            scenario == CircuitBreakerStressScenario.TtlExpired ? "0" : "15");
        return request;
    }

    private static CircuitBreakerStressScenario GetCircuitBreakerStressScenario(int requestorId)
        => (requestorId % 12) switch
        {
            0 => CircuitBreakerStressScenario.RetryAfter,
            1 => CircuitBreakerStressScenario.TtlExpired,
            2 => CircuitBreakerStressScenario.MaxAttempts,
            3 => CircuitBreakerStressScenario.NoRetryAfter,
            _ => CircuitBreakerStressScenario.Success
        };

    private static int ExpectedCircuitBreakerStressStatus(CircuitBreakerStressScenario scenario)
        => scenario switch
        {
            CircuitBreakerStressScenario.Success => 200,
            CircuitBreakerStressScenario.RetryAfter => 200,
            CircuitBreakerStressScenario.TtlExpired => 412,
            CircuitBreakerStressScenario.MaxAttempts => 412,
            CircuitBreakerStressScenario.NoRetryAfter => 429,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

    private static Dictionary<string, string> CreateCircuitBreakerStressProxyEnvironment() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["EVENT_LOGGERS"] = "file",
            ["LogToEvents"] = "exception",
            ["LogToConsole"] = "none",
            ["LogToAI"] = "none",
            ["LOG_LEVEL"] = "None",
            ["APPINSIGHTS_CONNECTIONSTRING"] = string.Empty,
            ["AsyncModeEnabled"] = "false",
            ["UseProfiles"] = "false",
            ["UserConfigRequired"] = "false",
            ["ValidateAuthAppID"] = "false",
            ["ValidateAuthConfig"] = "enabled=false, mode=none, header=S7P-KEY",
            ["Workers"] = "64",
            ["DefaultPriority"] = "1",
            ["PriorityKeyHeader"] = "S7PPriorityKey",
            ["PriorityKeys"] = "Success,RetryAfter,TtlExpired,MaxAttempts,NoRetryAfter",
            ["PriorityValues"] = "1,1,2,2,3",
            ["LoadBalanceMode"] = "roundrobin",
            ["IterationMode"] = "MultiPass",
            ["MaxAttempts"] = "3",
            ["UseSharedIterators"] = "false",
            ["MaxQueueLength"] = "20000",
            ["PollInterval"] = "500",
            ["PollTimeout"] = "1000",
            ["Timeout"] = "2000",
            ["DefaultTTLSecs"] = "15",
            ["CBErrorThreshold"] = "1000000",
            ["CBTimeslice"] = "2",
            ["Host1"] =
                "host=http://127.0.0.1:{BACKEND_A_PORT}; mode=direct; path=/; " +
                "processor=DefaultStream; enabled=true; retryafter=true",
            ["Host2"] =
                "host=http://127.0.0.1:{BACKEND_B_PORT}; mode=direct; path=/; " +
                "processor=DefaultStream; enabled=true; retryafter=true",
            ["Host3"] =
                "host=http://127.0.0.1:{BACKEND_C_PORT}; mode=direct; path=/noretry/*; " +
                "processor=DefaultStream; enabled=true; retryafter=false"
        };

    private static async Task WaitForStressProxyHostsAsync(
        LoggedProcess proxy,
        int proxyPort,
        int expectedHostCount,
        TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var stopwatch = Stopwatch.StartNew();
        string? lastResult = null;
        while (stopwatch.Elapsed < timeout)
        {
            if (proxy.HasExited)
            {
                throw new InvalidOperationException(
                    $"{proxy.Name} exited with code {proxy.ExitCode} before hosts became active." +
                    Environment.NewLine + proxy.GetLogTail());
            }

            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{proxyPort}/health");
                lastResult = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode &&
                    lastResult.Contains($"Hosts: {expectedHostCount}", StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastResult = exception.Message;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Timed out waiting for {expectedHostCount} proxy hosts. Last result: {lastResult ?? "none"}." +
            Environment.NewLine + proxy.GetLogTail());
    }

    private static string FormatCircuitBreakerStressReport(
        TimeSpan elapsed,
        CircuitBreakerStressMetrics metrics,
        IReadOnlyList<string> backendUrls,
        IReadOnlyList<Dictionary<string, int>> backendStats)
    {
        var builder = new StringBuilder()
            .AppendLine("Three-host circuit-breaker stress")
            .AppendLine($"Elapsed: {elapsed.TotalSeconds:F1}s")
            .AppendLine($"Started: {metrics.Started:N0}")
            .AppendLine($"Completed: {metrics.Completed:N0}")
            .AppendLine($"In flight: {metrics.InFlight:N0}")
            .AppendLine($"Unexpected: {metrics.Unexpected:N0}")
            .AppendLine($"Throughput: {metrics.Completed / elapsed.TotalSeconds:N0} requests/sec")
            .AppendLine();

        foreach (var scenario in Enum.GetValues<CircuitBreakerStressScenario>())
        {
            builder.Append(scenario)
                .Append(": started=").Append(metrics.StartedFor(scenario).ToString("N0"))
                .Append(" completed=").Append(metrics.CompletedFor(scenario).ToString("N0"))
                .Append(" outcomes=").AppendLine(metrics.FormatOutcomes(scenario));
        }

            foreach (var sample in metrics.UnexpectedSamples)
            {
                builder.Append("Unexpected sample: ").AppendLine(sample);
            }

        builder.AppendLine();
        for (int backendIndex = 0; backendIndex < backendUrls.Count; backendIndex++)
        {
            builder.Append("Backend ").Append((char)('A' + backendIndex))
                .Append(' ').Append(backendUrls[backendIndex]).Append(": ")
                .AppendLine(string.Join(", ", backendStats[backendIndex]
                    .OrderBy(item => item.Key)
                    .Select(item => $"{item.Key}={item.Value:N0}")));
        }

        return builder.ToString();
    }

    private enum CircuitBreakerStressScenario
    {
        Success,
        RetryAfter,
        TtlExpired,
        MaxAttempts,
        NoRetryAfter
    }

    private sealed class CircuitBreakerStressMetrics
    {
        private readonly ConcurrentDictionary<CircuitBreakerStressScenario, long> _started = new();
        private readonly ConcurrentDictionary<CircuitBreakerStressScenario, long> _completed = new();
        private readonly ConcurrentDictionary<(CircuitBreakerStressScenario Scenario, string Outcome), long> _outcomes = new();
        private readonly ConcurrentQueue<string> _unexpectedSamples = new();
        private long _totalStarted;
        private long _totalCompleted;
        private long _inFlight;
        private long _unexpected;

        internal long Started => Interlocked.Read(ref _totalStarted);
        internal long Completed => Interlocked.Read(ref _totalCompleted);
        internal long InFlight => Interlocked.Read(ref _inFlight);
        internal long Unexpected => Interlocked.Read(ref _unexpected);
        internal IReadOnlyList<string> UnexpectedSamples => _unexpectedSamples.ToArray();

        internal void Begin(CircuitBreakerStressScenario scenario)
        {
            Interlocked.Increment(ref _totalStarted);
            Interlocked.Increment(ref _inFlight);
            _started.AddOrUpdate(scenario, 1, static (_, count) => count + 1);
        }

        internal void Complete(CircuitBreakerStressScenario scenario, string outcome, string detail)
        {
            _outcomes.AddOrUpdate((scenario, outcome), 1, static (_, count) => count + 1);
            _completed.AddOrUpdate(scenario, 1, static (_, count) => count + 1);
            Interlocked.Increment(ref _totalCompleted);
            Interlocked.Decrement(ref _inFlight);

            if (!string.Equals(
                    outcome,
                    ExpectedCircuitBreakerStressStatus(scenario).ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _unexpected);
                if (_unexpectedSamples.Count < 20)
                {
                    _unexpectedSamples.Enqueue($"{scenario} outcome={outcome} {detail}");
                }
            }
        }

        internal long StartedFor(CircuitBreakerStressScenario scenario)
            => _started.GetValueOrDefault(scenario);

        internal long CompletedFor(CircuitBreakerStressScenario scenario)
            => _completed.GetValueOrDefault(scenario);

        internal long OutcomeCount(CircuitBreakerStressScenario scenario, int statusCode)
            => _outcomes.GetValueOrDefault((scenario, statusCode.ToString(CultureInfo.InvariantCulture)));

        internal string FormatOutcomes(CircuitBreakerStressScenario scenario)
            => string.Join(", ", _outcomes
                .Where(item => item.Key.Scenario == scenario)
                .OrderBy(item => item.Key.Outcome)
                .Select(item => $"{item.Key.Outcome}={item.Value:N0}"));
    }
}