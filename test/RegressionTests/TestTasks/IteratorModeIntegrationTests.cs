using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Test;

public sealed partial class PolicyScenarioIntegrationTests
{
    [TestMethod]
    public void PriorityGroupLoadBalanceMode_IsAccepted()
    {
        var config = new ProxyConfig
        {
            LoadBalanceMode = "prioritygroup"
        };

        ConfigParser.ApplyDerivedSettings(
            config,
            typeof(ProxyConfig).GetProperty(nameof(ProxyConfig.LoadBalanceMode))!);

        Assert.AreEqual(Constants.PriorityGroup, config.LoadBalanceMode);
    }

    [TestMethod]
    [RegressionTestCase(
        "round-robin-load-balancer",
        "Per-request mode controls retry breadth",
        "Sends failing requests with default, MultiPass, SinglePass, and invalid iterator headers and confirms 3, 5, 3, and 3 backend attempts.")]
    [TestCategory("Integration")]
    [TestCategory("Iterator")]
    [Timeout(180_000)]
    public async Task IteratorHeader_ControlsSharedIteratorAttemptsAcrossThreeBackends()
    {
        var config = IteratorTestLocalConfig.Load();
        var timeout = TimeSpan.FromSeconds(config.GetPositiveInt("ITERATOR_TEST_TIMEOUT_SECONDS", 30));
        var pythonExecutable = config.GetValue("ITERATOR_TEST_PYTHON", "python3");

        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-iterator-test-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Iterator test artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(4);
        var backendPorts = ports.Take(3).ToArray();
        var proxyPort = ports[3];
        var backendUrls = backendPorts
            .Select(port => $"http://127.0.0.1:{port}")
            .ToArray();

        var backendProcesses = new List<LoggedProcess>();
        LoggedProcess? proxy = null;
        try
        {
            for (int index = 0; index < backendPorts.Length; index++)
            {
                var name = $"iterator backend {(char)('A' + index)}";
                backendProcesses.Add(LoggedProcess.Start(
                    name,
                    CreateStreamServerStartInfo(pythonExecutable, streamServerPath, backendPorts[index]),
                    Path.Combine(artifactRoot, $"backend-{index + 1}.stdout.log"),
                    Path.Combine(artifactRoot, $"backend-{index + 1}.stderr.log")));
            }

            await Task.WhenAll(backendProcesses.Select((process, index) =>
                WaitUntilReadyAsync(
                    process,
                    new Uri($"{backendUrls[index]}/health"),
                    timeout)));

            var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
            proxy = LoggedProcess.Start(
                "iterator test proxy",
                CreateIteratorProxyStartInfo(
                    config.ProxyEnvironment,
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    backendPorts),
                Path.Combine(artifactRoot, "proxy.stdout.log"),
                Path.Combine(artifactRoot, "proxy.stderr.log"));

            await WaitUntilReadyAsync(
                proxy,
                new Uri($"http://127.0.0.1:{proxyPort}/startup"),
                timeout);
            await WaitForActiveBackendsAsync(proxy, eventLogPath, backendPorts.Length, timeout);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
                Timeout = timeout
            };

            var cases = new[]
            {
                new IteratorCase("default", null, 3, HttpStatusCode.InternalServerError, "\"Attempt-3\""),
                new IteratorCase("multi-pass", "MultiPass", 5, HttpStatusCode.PreconditionFailed, "Maximum backend attempts reached (5)."),
                new IteratorCase("single-pass", "SinglePass", 3, HttpStatusCode.InternalServerError, "\"Attempt-3\""),
                new IteratorCase("invalid", "not-a-mode", 3, HttpStatusCode.InternalServerError, "\"Attempt-3\"")
            };

            foreach (var testCase in cases)
            {
                var result = await RunIteratorCaseAsync(
                    client,
                    eventLogPath,
                    testCase,
                    backendUrls,
                    timeout);
                TestContext.WriteLine(
                    $"{testCase.Name}: attempts={result.Count}, " +
                    $"hosts={string.Join(" -> ", result.Select(attempt => attempt.BackendHost))}");
            }

        }
        finally
        {
            if (proxy != null)
            {
                await proxy.DisposeAsync();
            }

            foreach (var backend in backendProcesses)
            {
                await backend.DisposeAsync();
            }

            AttachScenarioArtifacts(artifactRoot);
        }
    }

    [TestMethod]
    [RegressionTestCase(
        "time-to-first-byte-load",
        "TTFB mode prefers the fastest active backend under real latency",
        "Starts one deliberately slow backend and two fast backends, sends multiple requests in timetofirstbyte mode, and confirms the slow backend is avoided.")]
    [TestCategory("Integration")]
    [TestCategory("Iterator")]
    [TestCategory("Load")]
    [Timeout(180_000)]
    public async Task TTFBMode_PrefersFastestBackend_WhenOneBackendIsSlow()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The TTFB load harness requires Linux or WSL with python3 available on PATH.");
        }

        var config = IteratorTestLocalConfig.Load();
        var timeout = TimeSpan.FromSeconds(config.GetPositiveInt("ITERATOR_TEST_TIMEOUT_SECONDS", 30));
        var pythonExecutable = config.GetValue("ITERATOR_TEST_PYTHON", "python3");
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        var streamServerPath = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");
        Assert.IsTrue(File.Exists(streamServerPath), $"Stream server not found: {streamServerPath}");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-ttfb-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"TTFB load artifacts: {artifactRoot}");

        var ports = GetAvailablePorts(4);
        var backendPorts = ports.Take(3).ToArray();
        var proxyPort = ports[3];
        var backendUrls = backendPorts.Select(port => $"http://127.0.0.1:{port}").ToArray();
        var backendProcesses = new List<LoggedProcess>();
        LoggedProcess? proxy = null;

        try
        {
            for (int index = 0; index < backendPorts.Length; index++)
            {
                var startInfo = CreateStreamServerStartInfo(
                    pythonExecutable,
                    streamServerPath,
                    backendPorts[index]);
                startInfo.Environment["NULL_SERVER_QUIET"] = "true";
                if (index == 0)
                {
                    startInfo.Environment["NULL_SERVER_DELAY_MS"] = "800";
                }
                else
                {
                    startInfo.Environment["NULL_SERVER_DELAY_MS"] = "0";
                }

                backendProcesses.Add(LoggedProcess.Start(
                    $"ttfb backend {(char)('A' + index)}",
                    startInfo,
                    Path.Combine(artifactRoot, $"backend-{index + 1}.stdout.log"),
                    Path.Combine(artifactRoot, $"backend-{index + 1}.stderr.log")));
            }

            await Task.WhenAll(backendProcesses.Select((process, index) =>
                WaitUntilReadyAsync(
                    process,
                    new Uri($"{backendUrls[index]}/health"),
                    timeout)));

            var proxyEnvironment = new Dictionary<string, string>(config.ProxyEnvironment, StringComparer.OrdinalIgnoreCase)
            {
                ["LoadBalanceMode"] = "timetofirstbyte",
                ["IterationMode"] = "SinglePass",
                ["UseSharedIterators"] = "true",
                ["MaxAttempts"] = "3",
                ["Host_iterator_a"] = $"host=http://127.0.0.1:{backendPorts[0]}; path=/; processor=DefaultStream; probe=/health; enabled=true",
                ["Host_iterator_b"] = $"host=http://127.0.0.1:{backendPorts[1]}; path=/; processor=DefaultStream; probe=/health; enabled=true",
                ["Host_iterator_c"] = $"host=http://127.0.0.1:{backendPorts[2]}; path=/; processor=DefaultStream; probe=/health; enabled=true"
            };

            var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
            proxy = LoggedProcess.Start(
                "ttfb proxy",
                CreateIteratorProxyStartInfo(
                    proxyEnvironment,
                    proxyAssembly,
                    proxyPort,
                    eventLogPath,
                    backendPorts),
                Path.Combine(artifactRoot, "proxy.stdout.log"),
                Path.Combine(artifactRoot, "proxy.stderr.log"));

            await WaitUntilReadyAsync(
                proxy,
                new Uri($"http://127.0.0.1:{proxyPort}/startup"),
                timeout);
            await WaitForActiveBackendsAsync(proxy, eventLogPath, backendPorts.Length, timeout);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
                Timeout = timeout
            };

            var requests = Enumerable.Range(0, 12)
                .Select(async _ => await client.GetAsync("/success"))
                .ToArray();

            var responses = await Task.WhenAll(requests);
            foreach (var response in responses)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                response.Dispose();
            }

            var backendCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(eventLogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!string.Equals(GetProperty(root, "Type"), "S7P-BackendRequest", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var backendHost = GetProperty(root, "Backend-Host");
                    if (!string.IsNullOrWhiteSpace(backendHost))
                    {
                        backendCounts[backendHost] = backendCounts.GetValueOrDefault(backendHost) + 1;
                    }
                }
                catch (JsonException)
                {
                    // Another process may still be writing to the log; ignore partial lines.
                }
            }

            var slowBackendHost = $"http://127.0.0.1:{backendPorts[0]}";
            var fastBackendHostCount = backendPorts
                .Skip(1)
                .Select(port => $"http://127.0.0.1:{port}")
                .Sum(host => backendCounts.GetValueOrDefault(host));
            var slowBackendCount = backendCounts.GetValueOrDefault(slowBackendHost);

            Assert.IsTrue(
                fastBackendHostCount > slowBackendCount,
                $"TTFB mode did not prefer the faster backend. Counts: {string.Join(", ", backendCounts.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
            Assert.IsTrue(
                slowBackendCount < 3,
                $"Slow backend was still selected too often under TTFB mode. Counts: {string.Join(", ", backendCounts.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }
        finally
        {
            if (proxy != null)
            {
                await proxy.DisposeAsync();
            }

            foreach (var backend in backendProcesses)
            {
                await backend.DisposeAsync();
            }

            AttachScenarioArtifacts(artifactRoot);
        }
    }

    private static async Task<IReadOnlyList<IteratorBackendAttempt>> RunIteratorCaseAsync(
        HttpClient client,
        string eventLogPath,
        IteratorCase testCase,
        IReadOnlyList<string> backendUrls,
        TimeSpan timeout)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/500error?case={Uri.EscapeDataString(testCase.Name)}")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (testCase.IteratorHeader != null)
        {
            request.Headers.TryAddWithoutValidation("S7P-Iterator", testCase.IteratorHeader);
        }

        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var responseHeaders = ReadResponseHeaders(response);
        var requestMid = FindHeader(responseHeaders, "x-MID");
        var responseAttempts = FindHeader(responseHeaders, "Attempts");

        Assert.AreEqual(testCase.ExpectedStatusCode, response.StatusCode, testCase.Name);
        Assert.IsTrue(
            responseBody.StartsWith("No active hosts were able to handle the request: ", StringComparison.Ordinal),
            $"{testCase.Name}: unexpected error prefix: {responseBody}");
        StringAssert.Contains(responseBody, testCase.ExpectedBodyText, testCase.Name);
        StringAssert.Contains(
            responseBody,
            $"\"Attempt-{testCase.ExpectedAttempts}\"",
            $"{testCase.Name}: response must list every backend attempt");
        Assert.IsFalse(string.IsNullOrWhiteSpace(requestMid), $"{testCase.Name}: x-MID response header is missing.");
        Assert.AreEqual(
            testCase.ExpectedAttempts.ToString(CultureInfo.InvariantCulture),
            responseAttempts,
            $"{testCase.Name}: response attempt count");

        var attempts = await WaitForIteratorAttemptsAsync(
            eventLogPath,
            requestMid!,
            testCase.ExpectedAttempts,
            timeout);

        Assert.AreEqual(testCase.ExpectedAttempts, attempts.Count, $"{testCase.Name}: backend event count");
        CollectionAssert.AreEqual(
            Enumerable.Range(1, testCase.ExpectedAttempts).ToArray(),
            attempts.Select(attempt => attempt.Attempt).ToArray(),
            $"{testCase.Name}: attempt numbers");

        var firstPass = attempts.Take(backendUrls.Count).Select(attempt => attempt.BackendHost).ToArray();
        CollectionAssert.AreEquivalent(
            backendUrls.ToArray(),
            firstPass,
            $"{testCase.Name}: first pass must use all three stream servers once");

        for (int index = backendUrls.Count; index < attempts.Count; index++)
        {
            Assert.AreEqual(
                firstPass[index % backendUrls.Count],
                attempts[index].BackendHost,
                $"{testCase.Name}: shared iterator must continue its circular backend order");
        }

        return attempts;
    }

    private static async Task<IReadOnlyList<IteratorBackendAttempt>> WaitForIteratorAttemptsAsync(
        string eventLogPath,
        string requestMid,
        int expectedCount,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<IteratorBackendAttempt> attempts = [];
        while (stopwatch.Elapsed < timeout)
        {
            attempts = ReadIteratorAttempts(eventLogPath, requestMid);
            if (attempts.Count >= expectedCount)
            {
                return attempts;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Timed out waiting for {expectedCount} backend events for {requestMid}; " +
            $"found {attempts.Count} in {eventLogPath}.");
    }

    private static async Task WaitForActiveBackendsAsync(
        LoggedProcess proxy,
        string eventLogPath,
        int expectedCount,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (proxy.HasExited)
            {
                throw new InvalidOperationException(
                    $"{proxy.Name} exited with code {proxy.ExitCode} before all backends became active." +
                    Environment.NewLine + proxy.GetLogTail());
            }

            if (File.Exists(eventLogPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(eventLogPath).Reverse())
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            var root = document.RootElement;
                            if (string.Equals(GetProperty(root, "Type"), "S7P-Backend", StringComparison.Ordinal) &&
                                int.TryParse(
                                    GetProperty(root, "ActiveHostsCount"),
                                    NumberStyles.None,
                                    CultureInfo.InvariantCulture,
                                    out var activeHosts) &&
                                activeHosts == expectedCount)
                            {
                                return;
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
            $"Timed out waiting for ActiveHostsCount={expectedCount} in {eventLogPath}." +
            Environment.NewLine + proxy.GetLogTail());
    }

    private static IReadOnlyList<IteratorBackendAttempt> ReadIteratorAttempts(
        string eventLogPath,
        string requestMid)
    {
        if (!File.Exists(eventLogPath))
        {
            return [];
        }

        var attempts = new List<IteratorBackendAttempt>();
        try
        {
            foreach (var line in File.ReadLines(eventLogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var type = GetProperty(root, "Type");
                    var mid = GetProperty(root, "MID");
                    if (!string.Equals(type, "S7P-BackendRequest", StringComparison.Ordinal) ||
                        mid?.StartsWith(requestMid + "-", StringComparison.Ordinal) != true ||
                        !int.TryParse(GetProperty(root, "Attempt"), NumberStyles.None, CultureInfo.InvariantCulture, out var attempt))
                    {
                        continue;
                    }

                    var backendHost = GetProperty(root, "Backend-Host");
                    if (!string.IsNullOrWhiteSpace(backendHost))
                    {
                        attempts.Add(new IteratorBackendAttempt(attempt, backendHost));
                    }
                }
                catch (JsonException)
                {
                    // Ignore a line that is still being written; the polling loop will read it again.
                }
            }
        }
        catch (IOException)
        {
            return [];
        }

        return attempts.OrderBy(attempt => attempt.Attempt).ToArray();
    }

    private static ProcessStartInfo CreateStreamServerStartInfo(
        string pythonExecutable,
        string streamServerPath,
        int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExecutable,
            WorkingDirectory = Path.GetDirectoryName(streamServerPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(streamServerPath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        return startInfo;
    }

    private static ProcessStartInfo CreateIteratorProxyStartInfo(
        IReadOnlyDictionary<string, string> proxyEnvironment,
        string proxyAssembly,
        int proxyPort,
        string eventLogPath,
        IReadOnlyList<int> backendPorts)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(proxyAssembly);

        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (Regex.IsMatch(key, "^(Host|Probe_path|IP)(\\d.*|[-_].*)$", RegexOptions.IgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment.Remove("AZURE_APPCONFIG_CONNECTION_STRING");
        startInfo.Environment.Remove("AZURE_APPCONFIG_ENDPOINT");
        startInfo.Environment.Remove("AZURE_APPCONFIG_LABEL");
        startInfo.Environment.Remove("APPENDHOSTSFILE");
        startInfo.Environment.Remove("AppendHostsFile");

        foreach (var setting in proxyEnvironment)
        {
            startInfo.Environment[setting.Key] = ExpandBackendPorts(setting.Value, backendPorts);
        }
        startInfo.Environment["Port"] = proxyPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["LOGFILE_NAME"] = eventLogPath;
        return startInfo;
    }

    private static string ExpandBackendPorts(string value, IReadOnlyList<int> backendPorts)
    {
        var expanded = value;
        for (int index = 0; index < backendPorts.Count; index++)
        {
            expanded = expanded.Replace(
                $"{{BACKEND_{(char)('A' + index)}_PORT}}",
                backendPorts[index].ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
        return expanded;
    }

    private static int[] GetAvailablePorts(int count)
    {
        var ports = new HashSet<int>();
        while (ports.Count < count)
        {
            ports.Add(GetAvailablePort());
        }
        return ports.ToArray();
    }

    private sealed class IteratorTestLocalConfig
    {
        public Dictionary<string, string> TestEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ProxyEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public static IteratorTestLocalConfig Load()
        {
            var basePath = Path.Combine(AppContext.BaseDirectory, "configs", "iterator-test.json");
            if (!File.Exists(basePath))
            {
                throw new FileNotFoundException(
                    "Iterator test base config was not found. Restore configs/iterator-test.json.",
                    basePath);
            }

            var config = JsonSerializer.Deserialize<IteratorTestLocalConfig>(
                File.ReadAllText(basePath),
                s_jsonOptions) ?? new IteratorTestLocalConfig();
            var configuredPath = Environment.GetEnvironmentVariable("ITERATOR_TEST_CONFIG_PATH");
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppContext.BaseDirectory, "configs", "iterator-test.local.json")
                : Path.GetFullPath(configuredPath);
            if (File.Exists(path))
            {
                var localConfig = JsonSerializer.Deserialize<IteratorTestLocalConfig>(
                    File.ReadAllText(path),
                    s_jsonOptions) ?? new IteratorTestLocalConfig();
                foreach (var setting in localConfig.TestEnvironment)
                {
                    config.TestEnvironment[setting.Key] = setting.Value;
                }
                foreach (var setting in localConfig.ProxyEnvironment)
                {
                    config.ProxyEnvironment[setting.Key] = setting.Value;
                }
            }

            foreach (var requiredKey in new[]
            {
                "EVENT_LOGGERS",
                "LogToEvents",
                "IterationMode",
                "MaxAttempts",
                "UseSharedIterators",
                "Host_iterator_a",
                "Host_iterator_b",
                "Host_iterator_c"
            })
            {
                if (!config.ProxyEnvironment.ContainsKey(requiredKey))
                {
                    throw new InvalidOperationException(
                        $"Merged iterator test config is missing proxyEnvironment.{requiredKey}.");
                }
            }
            return config;
        }

        public string GetValue(string name, string defaultValue)
            => Environment.GetEnvironmentVariable(name) ??
               (TestEnvironment.TryGetValue(name, out var value) ? value : defaultValue);

        public int GetPositiveInt(string name, int defaultValue)
            => int.TryParse(GetValue(name, defaultValue.ToString(CultureInfo.InvariantCulture)),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var value) && value > 0
                ? value
                : defaultValue;

        public bool GetBool(string name, bool defaultValue)
            => bool.TryParse(GetValue(name, defaultValue.ToString()), out var value)
                ? value
                : defaultValue;
    }

    private sealed record IteratorCase(
        string Name,
        string? IteratorHeader,
        int ExpectedAttempts,
        HttpStatusCode ExpectedStatusCode,
        string ExpectedBodyText);
    private sealed record IteratorBackendAttempt(int Attempt, string BackendHost);
}