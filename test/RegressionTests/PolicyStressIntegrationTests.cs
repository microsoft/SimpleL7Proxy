using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SimpleL7Proxy.Test;

public sealed partial class PolicyScenarioIntegrationTests
{
    private static readonly string[] s_stressPriorities = ["high", "medium", "low"];

    [TestMethod]
    [RegressionTestCase(
        "apim-policy-load",
        "Policy remains stable under sustained concurrency",
        "Runs 1,000 concurrent requestors through throttling and recovery and requires every started request to reach a terminal outcome.")]
    [TestCategory("Integration")]
    [TestCategory("APIMPolicy")]
    [TestCategory("Stress")]
    [Timeout(2_700_000)]
    public async Task V31Policy_SustainsOneThousandRequestorsForThirtyMinutes()
    {
        var localConfig = PolicyTestLocalConfig.Load();
        if (localConfig == null)
        {
            Assert.Inconclusive(
                "Create test/RegressionTests/configs/policy-test.local.json with proxyEnvironment.Host_apim before running the APIM policy stress test.");
            return;
        }

        localConfig.ApplyTestEnvironmentDefaults();
        var policySettings = PolicyTestSettings.FromEnvironment(localConfig.ProxyEnvironment);
        if (policySettings == null)
        {
            Assert.Inconclusive("Set POLICY_TEST_APIM_URL before running the APIM policy stress test.");
            return;
        }

        var stressSettings = PolicyStressSettings.FromEnvironment();
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");

        var runId = $"stress-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..39];
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"simplel7proxy-policy-stress-{runId}");
        Directory.CreateDirectory(artifactRoot);
        var reportPath = ResolveStressReportPath(artifactRoot);
        var artifactReportPath = Path.Combine(artifactRoot, "stress-report.txt");
        var finalSimulatorStatsPath = Path.Combine(artifactRoot, "simulator-final-stats.json");
        var proxyOutputPath = Path.Combine(artifactRoot, "proxy.stdout.log");
        var proxyErrorPath = Path.Combine(artifactRoot, "proxy.stderr.log");
        var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");

        await File.WriteAllTextAsync(
            reportPath,
            FormatStressHeader(runId, stressSettings),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TestContext.WriteLine($"Policy stress artifacts: {artifactRoot}");
        TestContext.WriteLine($"Live stress report: {reportPath}");

        var proxyPort = GetAvailablePort();
        var proxyStartInfo = CreateProxyStartInfo(
            policySettings,
            proxyAssembly,
            proxyPort,
            eventLogPath);
        proxyStartInfo.Environment["EVENT_LOGGERS"] = "none";
        proxyStartInfo.Environment["LOG_LEVEL"] = "Warning";
        proxyStartInfo.Environment["MaxQueueLength"] = Math.Max(
            5_000,
            stressSettings.RequestorCount * 2).ToString(CultureInfo.InvariantCulture);
        proxyStartInfo.Environment["Timeout"] = ((int)stressSettings.RequestTimeout.TotalMilliseconds)
            .ToString(CultureInfo.InvariantCulture);

        await using var proxy = LoggedProcess.Start(
            "policy stress proxy",
            proxyStartInfo,
            proxyOutputPath,
            proxyErrorPath);

        await WaitUntilReadyAsync(
            proxy,
            new Uri($"http://127.0.0.1:{proxyPort}/readiness"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));

        using var loadHandler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = stressSettings.RequestorCount,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        using var loadClient = new HttpClient(loadHandler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var controlClient = new HttpClient
        {
            BaseAddress = policySettings.SimulatorBaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

        await WarmSimulatorAsync(
            new Uri(policySettings.SimulatorBaseAddress, "/api/health"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));
        await WarmStressEndpointAsync(controlClient);
        await ResetSimulatorStressRunAsync(controlClient, runId);
        await ResetPolicyStateAsync(loadClient, policySettings.ProxyRoutePrefix);

        var metrics = new PolicyStressMetrics();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "policy-stress",
            messages = new[] { new { role = "user", content = "sustained policy stress" } },
            stream = false
        });
        var startedAtUtc = DateTime.UtcNow;
        var deadlineUtc = startedAtUtc + stressSettings.Duration;
        using var reportCancellation = new CancellationTokenSource();
        var reportTask = ReportStressAsync(
            controlClient,
            runId,
            stressSettings,
            metrics,
            startedAtUtc,
            reportPath,
            reportCancellation.Token);

        var requestors = Enumerable.Range(0, stressSettings.RequestorCount)
            .Select(index => RunStressRequestorAsync(
                index,
                s_stressPriorities[index % s_stressPriorities.Length],
                runId,
                policySettings.ProxyRoutePrefix,
                body,
                deadlineUtc,
                stressSettings,
                loadClient,
                metrics))
            .ToArray();

        await Task.WhenAll(requestors);
        reportCancellation.Cancel();
        try
        {
            await reportTask;
        }
        catch (OperationCanceledException)
        {
        }

        var finalMetrics = metrics.Capture(DateTime.UtcNow);
        var finalSimulatorStats = await GetSimulatorStressStatsAsync(controlClient, runId, metrics);
        await File.WriteAllTextAsync(
            finalSimulatorStatsPath,
            JsonSerializer.Serialize(finalSimulatorStats, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var finalReport = FormatStressSnapshot(
            "FINAL",
            startedAtUtc,
            finalMetrics,
            previous: null,
            finalSimulatorStats,
            metrics.GetInstances());
        await AppendStressReportAsync(reportPath, finalReport);
        TestContext.WriteLine(finalReport);

        await ResetSimulatorStressRunAsync(controlClient, runId);
        await proxy.StopAsync();

        if (!string.Equals(reportPath, artifactReportPath, StringComparison.Ordinal))
        {
            File.Copy(reportPath, artifactReportPath, overwrite: true);
        }

        AttachScenarioArtifacts(artifactRoot);

        var totals = finalMetrics.Priorities.Single(priority => priority.Priority == "ALL");
        var medium = finalMetrics.Priorities.Single(priority => priority.Priority == "medium");
        Assert.IsTrue(totals.Started > 0, "The stress test did not start any requests.");
        Assert.AreEqual(totals.Started, totals.Completed, "Every started request must reach a terminal client outcome.");
        Assert.AreEqual(0L, totals.InFlight, "No requests may remain in flight after the drain phase.");
        Assert.AreEqual(
            0L,
            medium.Failed,
            "Medium-priority requests must requeue until they complete or reach their request TTL.");
        Assert.IsTrue(
            finalMetrics.Priorities
                .Where(priority => priority.Priority != "ALL")
                .All(priority => priority.Completed > 0),
            "Every priority must complete requests.");
        Assert.IsTrue(finalSimulatorStats.Endpoints.Count > 0, "The simulator did not report any stress endpoints.");
        Assert.IsTrue(
            metrics.GetInstances().Count <= 1,
            "The in-memory TPM test requires one simulator replica; multiple X-Sim-Instance values were observed: " +
            string.Join(", ", metrics.GetInstances()));
    }

    private static async Task RunStressRequestorAsync(
        int requestorId,
        string priority,
        string runId,
        string proxyRoutePrefix,
        byte[] body,
        DateTime deadlineUtc,
        PolicyStressSettings settings,
        HttpClient client,
        PolicyStressMetrics metrics)
    {
        long sequence = 0;
        while (DateTime.UtcNow < deadlineUtc)
        {
            sequence++;
            metrics.Begin(priority);
            var started = Stopwatch.GetTimestamp();
            string outcome;
            var successful = false;
            var incomplete = false;

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{proxyRoutePrefix}/stress/{runId}/r{requestorId:D4}/openai/v1/chat/completions?sequence={sequence}");
                request.Headers.TryAddWithoutValidation("S7PPriorityKey", priority);
                request.Headers.TryAddWithoutValidation(
                    "S7PTTL",
                    settings.RequestTtlSeconds.ToString(CultureInfo.InvariantCulture));
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };

                using var requestTimeout = new CancellationTokenSource(settings.RequestTimeout);
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);
                await response.Content.CopyToAsync(Stream.Null, requestTimeout.Token);
                var statusCode = (int)response.StatusCode;
                outcome = statusCode.ToString(CultureInfo.InvariantCulture);
                successful = statusCode is >= 200 and < 300;
                incomplete = response.StatusCode == HttpStatusCode.TooManyRequests;
                if (response.Headers.TryGetValues("X-Sim-Instance", out var instances))
                {
                    foreach (var instance in instances)
                    {
                        metrics.AddInstance(instance);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                outcome = "timeout";
            }
            catch (HttpRequestException exception)
            {
                outcome = "http-" + exception.HttpRequestError;
            }
            catch (Exception exception)
            {
                outcome = "exception-" + exception.GetType().Name;
            }

            metrics.Complete(priority, successful, incomplete, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task ReportStressAsync(
        HttpClient controlClient,
        string runId,
        PolicyStressSettings settings,
        PolicyStressMetrics metrics,
        DateTime startedAtUtc,
        string reportPath,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(settings.ReportInterval);
        PolicyStressMetricsSnapshot? previous = null;
        var minute = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            minute++;
            var current = metrics.Capture(DateTime.UtcNow);
            SimulatorStressRunSnapshot simulatorStats;
            try
            {
                simulatorStats = await GetSimulatorStressStatsAsync(controlClient, runId, metrics);
            }
            catch (Exception exception)
            {
                simulatorStats = new SimulatorStressRunSnapshot
                {
                    RunId = runId,
                    Error = exception.Message
                };
            }

            var report = FormatStressSnapshot(
                $"MINUTE {minute}",
                startedAtUtc,
                current,
                previous,
                simulatorStats,
                metrics.GetInstances());
            await AppendStressReportAsync(reportPath, report);
            TestContext.WriteLine(report);
            previous = current;
        }
    }

    private static async Task WarmStressEndpointAsync(HttpClient client)
    {
        const string warmRunId = "warmup";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/policy-stress/warmup/stress/warmup/openai/v1/chat/completions")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Policy stress simulator warm-up failed: HTTP {(int)response.StatusCode}, body={body}");
        }
        await ResetSimulatorStressRunAsync(client, warmRunId);
    }

    private static async Task ResetSimulatorStressRunAsync(HttpClient client, string runId)
    {
        using var response = await client.DeleteAsync(
            "/api/policy-stress-runs/" + Uri.EscapeDataString(runId));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Policy stress simulator reset failed: HTTP {(int)response.StatusCode}, body={body}");
        }
    }

    private static async Task<SimulatorStressRunSnapshot> GetSimulatorStressStatsAsync(
        HttpClient client,
        string runId,
        PolicyStressMetrics metrics)
    {
        using var response = await client.GetAsync(
            "/api/policy-stress-runs/" + Uri.EscapeDataString(runId));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Policy stress simulator stats failed: HTTP {(int)response.StatusCode}, body={body}");
        }
        if (response.Headers.TryGetValues("X-Sim-Instance", out var instances))
        {
            foreach (var instance in instances)
            {
                metrics.AddInstance(instance);
            }
        }

        return await response.Content.ReadFromJsonAsync<SimulatorStressRunSnapshot>(s_jsonOptions)
            ?? throw new InvalidOperationException("Policy stress simulator returned an empty stats payload.");
    }

    private static string ResolveStressReportPath(string artifactRoot)
    {
        var configured = Environment.GetEnvironmentVariable("POLICY_STRESS_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(artifactRoot, "stress-report.txt");
        }

        var path = Path.GetFullPath(configured);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string FormatStressHeader(string runId, PolicyStressSettings settings)
    {
        return $"Policy stress run: {runId}{Environment.NewLine}" +
            $"Started: {DateTime.UtcNow:O}{Environment.NewLine}" +
            $"Requestors: {settings.RequestorCount} " +
            $"(high={CountRequestors(settings.RequestorCount, 0)}, " +
            $"medium={CountRequestors(settings.RequestorCount, 1)}, " +
            $"low={CountRequestors(settings.RequestorCount, 2)}){Environment.NewLine}" +
            $"Duration: {settings.Duration}{Environment.NewLine}" +
            $"Report interval: {settings.ReportInterval}{Environment.NewLine}" +
            $"Request TTL: {settings.RequestTtlSeconds} seconds{Environment.NewLine}" +
            $"Request timeout: {settings.RequestTimeout}{Environment.NewLine}" +
            "Success: terminal HTTP 2xx; Incomplete: terminal HTTP 429; " +
            "Failed: other terminal non-2xx or client exception." +
            Environment.NewLine + Environment.NewLine;
    }

    private static int CountRequestors(int total, int offset) =>
        total <= offset ? 0 : ((total - 1 - offset) / s_stressPriorities.Length) + 1;

    private static string FormatStressSnapshot(
        string title,
        DateTime startedAtUtc,
        PolicyStressMetricsSnapshot current,
        PolicyStressMetricsSnapshot? previous,
        SimulatorStressRunSnapshot simulator,
        IReadOnlyCollection<string> instances)
    {
        var builder = new StringBuilder();
        builder.Append(title)
            .Append(" @ ").Append(current.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append(" elapsed=").Append((current.CapturedAtUtc - startedAtUtc).ToString("c", CultureInfo.InvariantCulture))
            .AppendLine();
        builder.AppendLine("Priority  Started  Completed  InFlight  2xx  Incomplete  Failed  AvgMs  MaxMs  Outcomes");
        foreach (var priority in current.Priorities)
        {
            var old = previous?.Priorities.FirstOrDefault(item => item.Priority == priority.Priority);
            var completedDelta = priority.Completed - (old?.Completed ?? 0);
            var latencyDelta = priority.TotalLatencyMilliseconds - (old?.TotalLatencyMilliseconds ?? 0);
            var average = completedDelta == 0 ? 0 : latencyDelta / (double)completedDelta;
            builder.Append(priority.Priority.PadRight(8)).Append("  ")
                .Append(priority.Started.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append("  ")
                .Append(priority.Completed.ToString(CultureInfo.InvariantCulture).PadLeft(9)).Append("  ")
                .Append(priority.InFlight.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append("  ")
                .Append(priority.Successful.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append("  ")
                .Append(priority.Incomplete.ToString(CultureInfo.InvariantCulture).PadLeft(10)).Append("  ")
                .Append(priority.Failed.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append("  ")
                .Append(average.ToString("F1", CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
                .Append(priority.MaxLatencyMilliseconds.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("  ")
                .AppendLine(string.Join(',', priority.Outcomes.Select(item => $"{item.Key}:{item.Value}")));
        }

        if (!string.IsNullOrWhiteSpace(simulator.Error))
        {
            builder.Append("Simulator stats error: ").AppendLine(simulator.Error);
        }
        else
        {
            builder.AppendLine("Simulator endpoint  WindowTokens  WindowAccepted  Window429  TotalTokens  TotalAccepted  Total429");
            foreach (var endpoint in simulator.Endpoints)
            {
                builder.Append(endpoint.EndpointId.PadRight(18)).Append("  ")
                    .Append(endpoint.CurrentMinute.TokensReturned.ToString(CultureInfo.InvariantCulture).PadLeft(12)).Append("  ")
                    .Append(endpoint.CurrentMinute.Accepted.ToString(CultureInfo.InvariantCulture).PadLeft(14)).Append("  ")
                    .Append(endpoint.CurrentMinute.Throttled.ToString(CultureInfo.InvariantCulture).PadLeft(9)).Append("  ")
                    .Append(endpoint.Totals.TokensReturned.ToString(CultureInfo.InvariantCulture).PadLeft(11)).Append("  ")
                    .Append(endpoint.Totals.Accepted.ToString(CultureInfo.InvariantCulture).PadLeft(13)).Append("  ")
                    .AppendLine(endpoint.Totals.Throttled.ToString(CultureInfo.InvariantCulture).PadLeft(8));
            }
        }
        builder.Append("Simulator instances: ")
            .AppendLine(instances.Count == 0 ? "none observed" : string.Join(',', instances.Order(StringComparer.Ordinal)));
        builder.AppendLine();
        return builder.ToString();
    }

    private static Task AppendStressReportAsync(string path, string report) =>
        File.AppendAllTextAsync(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private sealed class PolicyStressSettings
    {
        public int RequestorCount { get; init; }
        public TimeSpan Duration { get; init; }
        public TimeSpan ReportInterval { get; init; }
        public TimeSpan RequestTimeout { get; init; }
        public int RequestTtlSeconds { get; init; }

        public static PolicyStressSettings FromEnvironment()
        {
            return new PolicyStressSettings
            {
                RequestorCount = ReadInt("POLICY_STRESS_REQUESTORS", 1_000, 1, 10_000),
                Duration = TimeSpan.FromSeconds(ReadInt("POLICY_STRESS_DURATION_SECONDS", 70, 1, 86_400)),
                ReportInterval = TimeSpan.FromSeconds(ReadInt("POLICY_STRESS_REPORT_INTERVAL_SECONDS", 15, 1, 3_600)),
                RequestTimeout = TimeSpan.FromSeconds(ReadInt("POLICY_STRESS_REQUEST_TIMEOUT_SECONDS", 210, 1, 3_600)),
                RequestTtlSeconds = ReadInt("POLICY_STRESS_REQUEST_TTL_SECONDS", 180, 1, 3_600)
            };
        }

        private static int ReadInt(string name, int defaultValue, int minimum, int maximum)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < minimum || value > maximum)
            {
                throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
            }
            return value;
        }
    }

    private sealed class PolicyStressMetrics
    {
        private readonly IReadOnlyDictionary<string, PolicyStressMetricBucket> _priorities =
            s_stressPriorities.ToDictionary(
                priority => priority,
                _ => new PolicyStressMetricBucket(),
                StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _instances = new(StringComparer.Ordinal);

        public void Begin(string priority) => _priorities[priority].Begin();

        public void Complete(string priority, bool successful, bool incomplete, string outcome, TimeSpan elapsed) =>
            _priorities[priority].Complete(successful, incomplete, outcome, elapsed);

        public void AddInstance(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _instances.TryAdd(value.Trim(), 0);
            }
        }

        public IReadOnlyCollection<string> GetInstances() => _instances.Keys.ToArray();

        public PolicyStressMetricsSnapshot Capture(DateTime capturedAtUtc)
        {
            var priorities = s_stressPriorities
                .Select(priority => _priorities[priority].Capture(priority))
                .ToList();
            priorities.Add(PolicyStressPrioritySnapshot.Combine(priorities));
            return new PolicyStressMetricsSnapshot(capturedAtUtc, priorities);
        }
    }

    private sealed class PolicyStressMetricBucket
    {
        private long _started;
        private long _completed;
        private long _inFlight;
        private long _successful;
        private long _incomplete;
        private long _failed;
        private long _totalLatencyMilliseconds;
        private long _maxLatencyMilliseconds;
        private readonly ConcurrentDictionary<string, long> _outcomes = new(StringComparer.Ordinal);

        public void Begin()
        {
            Interlocked.Increment(ref _started);
            Interlocked.Increment(ref _inFlight);
        }

        public void Complete(bool successful, bool incomplete, string outcome, TimeSpan elapsed)
        {
            var elapsedMilliseconds = Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds));
            Interlocked.Increment(ref _completed);
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Add(ref _totalLatencyMilliseconds, elapsedMilliseconds);
            UpdateMaximum(ref _maxLatencyMilliseconds, elapsedMilliseconds);
            if (successful)
            {
                Interlocked.Increment(ref _successful);
            }
            else if (incomplete)
            {
                Interlocked.Increment(ref _incomplete);
            }
            else
            {
                Interlocked.Increment(ref _failed);
            }
            _outcomes.AddOrUpdate(outcome, 1, static (_, current) => current + 1);
        }

        public PolicyStressPrioritySnapshot Capture(string priority) => new(
            priority,
            Interlocked.Read(ref _started),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _inFlight),
            Interlocked.Read(ref _successful),
            Interlocked.Read(ref _incomplete),
            Interlocked.Read(ref _failed),
            Interlocked.Read(ref _totalLatencyMilliseconds),
            Interlocked.Read(ref _maxLatencyMilliseconds),
            _outcomes.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));

        private static void UpdateMaximum(ref long target, long value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }

    private sealed record PolicyStressMetricsSnapshot(
        DateTime CapturedAtUtc,
        IReadOnlyList<PolicyStressPrioritySnapshot> Priorities);

    private sealed record PolicyStressPrioritySnapshot(
        string Priority,
        long Started,
        long Completed,
        long InFlight,
        long Successful,
        long Incomplete,
        long Failed,
        long TotalLatencyMilliseconds,
        long MaxLatencyMilliseconds,
        IReadOnlyDictionary<string, long> Outcomes)
    {
        public static PolicyStressPrioritySnapshot Combine(IReadOnlyCollection<PolicyStressPrioritySnapshot> values)
        {
            var outcomes = values
                .SelectMany(value => value.Outcomes)
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Value), StringComparer.Ordinal);
            return new PolicyStressPrioritySnapshot(
                "ALL",
                values.Sum(value => value.Started),
                values.Sum(value => value.Completed),
                values.Sum(value => value.InFlight),
                values.Sum(value => value.Successful),
                values.Sum(value => value.Incomplete),
                values.Sum(value => value.Failed),
                values.Sum(value => value.TotalLatencyMilliseconds),
                values.Max(value => value.MaxLatencyMilliseconds),
                outcomes);
        }
    }

    private sealed class SimulatorStressRunSnapshot
    {
        public string RunId { get; init; } = string.Empty;
        public DateTime GeneratedAtUtc { get; init; }
        public List<SimulatorStressEndpointSnapshot> Endpoints { get; init; } = [];
        public string? Error { get; init; }
    }

    private sealed class SimulatorStressEndpointSnapshot
    {
        public string EndpointId { get; init; } = string.Empty;
        public int TokenLimit { get; init; }
        public int ResponseTokens { get; init; }
        public SimulatorStressMinuteSnapshot CurrentMinute { get; init; } = new();
        public SimulatorStressTotalsSnapshot Totals { get; init; } = new();
        public List<SimulatorStressMinuteSnapshot> CompletedMinutes { get; init; } = [];
    }

    private sealed class SimulatorStressMinuteSnapshot
    {
        public DateTime WindowStartUtc { get; init; }
        public long Requests { get; init; }
        public long Accepted { get; init; }
        public long Throttled { get; init; }
        public long TokensReturned { get; init; }
    }

    private sealed class SimulatorStressTotalsSnapshot
    {
        public long Requests { get; init; }
        public long Accepted { get; init; }
        public long Throttled { get; init; }
        public long TokensReturned { get; init; }
    }
}