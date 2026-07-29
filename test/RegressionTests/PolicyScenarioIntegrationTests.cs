using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Calls a dedicated APIM API through a locally started SimpleL7Proxy and validates the
/// v3.1 policy from HTTP response headers and proxy NDJSON event records.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed partial class PolicyScenarioIntegrationTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["apim-failover"] = new(
                "Traffic Routing",
                "APIM policy failover",
                "Confirms configured APIM failover paths return the intended result before policy changes reach production traffic."),
            ["round-robin-load-balancer"] = new(
                "Traffic Routing",
                "Round-robin load balancer",
                "Ensures requests rotate across configured backends while honoring per-request SinglePass and MultiPass retry behavior."),
            ["apim-policy-load"] = new(
                "Reliability & Capacity",
                "APIM policy under load",
                "Confirms the APIM policy can sustain concurrent demand, throttling, and recovery without losing started requests.")
        };

    private const int StartupTimeoutSeconds = 180;
    private const int DefaultRequestTimeoutSeconds = 45;
    private const int EventFlushMilliseconds = 2_500;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [RegressionTestCase(
        "apim-failover",
        "Configured failover scenarios return expected outcomes",
        "Runs every configured APIM policy scenario and checks response status, backend attempts, correlation, and backend decision logs.")]
    [TestCategory("Integration")]
    [TestCategory("APIMPolicy")]
    [Timeout(1_200_000)]
    public async Task V31Policy_AllConfiguredScenariosMatchExpectedBehavior()
    {
        var localConfig = PolicyTestLocalConfig.Load();
        if (localConfig == null)
        {
            Assert.Inconclusive(
                "Create test/RegressionTests/configs/policy-test.local.json with proxyEnvironment.Host_apim before running the APIM policy tests.");
            return;
        }

        localConfig.ApplyTestEnvironmentDefaults();
        var settings = PolicyTestSettings.FromEnvironment(localConfig.ProxyEnvironment);
        if (settings == null)
        {
            Assert.Inconclusive(
                "Set POLICY_TEST_APIM_URL to the proxy-relative APIM route, for example policytest, " +
                "in the checked-in base config or shell environment.");
            return;
        }

        var catalogPath = Path.Combine(AppContext.BaseDirectory, "configs", "policy-scenarios.json");
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        Assert.IsTrue(File.Exists(catalogPath), $"Scenario catalog not found: {catalogPath}");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly not found: {proxyAssembly}");

        var catalog = JsonSerializer.Deserialize<ScenarioCatalog>(
            await File.ReadAllTextAsync(catalogPath),
            s_jsonOptions);
        Assert.IsNotNull(catalog);
        Assert.IsTrue(catalog.Scenarios.Count > 0, "The policy scenario catalog is empty.");
        var scenarios = string.IsNullOrWhiteSpace(settings.ScenarioName)
            ? catalog.Scenarios
            : catalog.Scenarios
                .Where(scenario => string.Equals(scenario.Name, settings.ScenarioName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        Assert.IsTrue(
            scenarios.Count > 0,
            $"No policy scenario named '{settings.ScenarioName}' exists in the catalog.");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-policy-tests-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactRoot);
        TestContext.WriteLine($"Policy test artifacts: {artifactRoot}");

        var eventLogPath = Path.Combine(artifactRoot, "events.ndjson");
        var proxyOutputPath = Path.Combine(artifactRoot, "proxy.stdout.log");
        var proxyErrorPath = Path.Combine(artifactRoot, "proxy.stderr.log");
        var proxyPort = GetAvailablePort();
        var startInfo = CreateProxyStartInfo(
            settings,
            proxyAssembly,
            proxyPort,
            eventLogPath);

        await using var proxy = LoggedProcess.Start(
            "policy test proxy",
            startInfo,
            proxyOutputPath,
            proxyErrorPath);

        await WaitUntilReadyAsync(
            proxy,
            new Uri($"http://127.0.0.1:{proxyPort}/readiness"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}"),
            Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
        };

        await WarmSimulatorAsync(
            new Uri(settings.SimulatorBaseAddress, "/api/health"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));

        var failures = new List<string>();
        foreach (var scenario in scenarios)
        {
            TestContext.WriteLine($"Running {scenario.Name}");
            try
            {
                var result = await RunScenarioAsync(
                    settings,
                    artifactRoot,
                    scenario,
                    client,
                    eventLogPath);
                if (result.Errors.Count == 0)
                {
                    TestContext.WriteLine(
                        $"PASS {scenario.Name}: HTTP {result.StatusCode}, " +
                        $"attempts={result.BackendAttempts ?? "missing"}");
                    if (!settings.KeepArtifacts)
                    {
                        Directory.Delete(result.ArtifactDirectory, recursive: true);
                    }
                }
                else
                {
                    failures.Add($"{scenario.Name}: {string.Join("; ", result.Errors)}");
                    AttachScenarioArtifacts(result.ArtifactDirectory);
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{scenario.Name}: harness failure: {exception.Message}");
                var scenarioDirectory = Path.Combine(artifactRoot, SanitizePathPart(scenario.Name));
                AttachScenarioArtifacts(scenarioDirectory);
            }
        }

        await proxy.StopAsync();

        if (failures.Count == 0 && !settings.KeepArtifacts && Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }

        if (failures.Count > 0)
        {
            TestContext.AddResultFile(proxyOutputPath);
            TestContext.AddResultFile(proxyErrorPath);
            Assert.Fail(
                $"{failures.Count} of {scenarios.Count} policy scenarios failed:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(failure => "- " + failure)) +
                Environment.NewLine +
                $"Artifacts retained at {artifactRoot}");
        }
    }

    private async Task<ScenarioResult> RunScenarioAsync(
        PolicyTestSettings settings,
        string artifactRoot,
        ScenarioDefinition scenario,
        HttpClient client,
        string suiteEventLogPath)
    {
        var artifactDirectory = Path.Combine(artifactRoot, SanitizePathPart(scenario.Name));
        Directory.CreateDirectory(artifactDirectory);

        var eventLogPath = Path.Combine(artifactDirectory, "events.ndjson");
        var responsePath = Path.Combine(artifactDirectory, "response.json");

        var proxyRoutePrefix = settings.ProxyRoutePrefix;
        await ResetPolicyStateAsync(client, proxyRoutePrefix);

        var encodedA = EncodeSpec(scenario.A);
        var encodedB = EncodeSpec(scenario.B);
        var requestPath = $"{proxyRoutePrefix}/{Uri.EscapeDataString(scenario.Name)}/{encodedA}/{encodedB}/openai/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, requestPath)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = scenario.Model,
                    messages = new[] { new { role = "user", content = "policy scenario " + scenario.Name } },
                    stream = string.Equals(scenario.A.Body, "sse", StringComparison.OrdinalIgnoreCase)
                }),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.TryAddWithoutValidation("S7PPriorityKey", scenario.PriorityKey);
        request.Headers.TryAddWithoutValidation("S7PDEBUG", "true");
        if (scenario.TtlSeconds is > 0)
        {
            request.Headers.TryAddWithoutValidation("S7PTTL", scenario.TtlSeconds.Value.ToString(CultureInfo.InvariantCulture));
        }

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();

        var responseHeaders = ReadResponseHeaders(response);
        var responseRecord = new
        {
            scenario = scenario.Name,
            status = (int)response.StatusCode,
            elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            headers = responseHeaders,
            body
        };
        await File.WriteAllTextAsync(
            responsePath,
            JsonSerializer.Serialize(responseRecord, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        await Task.Delay(EventFlushMilliseconds);

        var s7pId = FindHeader(responseHeaders, "S7P-ID") ?? FindHeader(responseHeaders, "x-MID");
        var events = ReadEvents(suiteEventLogPath, s7pId, scenario.Name);
        await File.WriteAllLinesAsync(
            eventLogPath,
            events.Select(proxyEvent => proxyEvent.GetRawText()),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var combinedBackendLog = BuildCombinedBackendLog(responseHeaders, events);
        var attempts = FindEvidenceValue(responseHeaders, events, "x-Backend-Attempts");
        var errors = ValidateScenario(
            scenario,
            (int)response.StatusCode,
            attempts,
            combinedBackendLog,
            responseHeaders,
            events);

        return new ScenarioResult(
            artifactDirectory,
            (int)response.StatusCode,
            attempts,
            errors);
    }

    private static ProcessStartInfo CreateProxyStartInfo(
        PolicyTestSettings settings,
        string proxyAssembly,
        int proxyPort,
        string eventLogPath)
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

        foreach (var setting in settings.ProxyEnvironment)
        {
            startInfo.Environment[setting.Key] =
                Environment.GetEnvironmentVariable(setting.Key) ?? setting.Value;
        }
        startInfo.Environment["Port"] = proxyPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["LOGFILE_NAME"] = eventLogPath;
        startInfo.Environment["Timeout"] = (settings.RequestTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static async Task ResetPolicyStateAsync(HttpClient client, string proxyRoutePrefix)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, proxyRoutePrefix + "/reset");
        using var response = await client.SendAsync(request);
        var resetHeader = response.Headers.TryGetValues("X-Policy-Test-Reset", out var values)
            ? values.FirstOrDefault()
            : null;

        if (response.StatusCode != HttpStatusCode.NoContent ||
            !string.Equals(resetHeader, "true", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"OPTIONS reset failed: HTTP {(int)response.StatusCode}, " +
                $"X-Policy-Test-Reset={resetHeader ?? "missing"}, body={body}");
        }
    }

    private static IReadOnlyList<string> ValidateScenario(
        ScenarioDefinition scenario,
        int statusCode,
        string? attempts,
        string backendLog,
        IReadOnlyDictionary<string, string> responseHeaders,
        IReadOnlyList<JsonElement> events)
    {
        var errors = new List<string>();
        if (statusCode != scenario.Expected.Status)
        {
            errors.Add($"expected HTTP {scenario.Expected.Status}, got {statusCode}");
        }

        if (scenario.Expected.BackendAttempts.HasValue &&
            !string.Equals(
                attempts,
                scenario.Expected.BackendAttempts.Value.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            errors.Add(
                $"expected x-Backend-Attempts={scenario.Expected.BackendAttempts}, " +
                $"got {attempts ?? "missing"}");
        }

        foreach (var required in scenario.Expected.RequiredLog)
        {
            if (!backendLog.Contains(required, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"backendLog missing '{required}'");
            }
        }

        foreach (var forbidden in scenario.Expected.ForbiddenLog)
        {
            if (backendLog.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"backendLog unexpectedly contains '{forbidden}'");
            }
        }

        if (events.Count == 0)
        {
            errors.Add("no correlated proxy NDJSON events were found");
        }

        var hasResponseCorrelation =
            !string.IsNullOrWhiteSpace(FindHeader(responseHeaders, "S7P-ID")) ||
            !string.IsNullOrWhiteSpace(FindHeader(responseHeaders, "x-MID"));
        var hasEventCorrelation = events.Any(proxyEvent =>
            !string.IsNullOrWhiteSpace(GetProperty(proxyEvent, "S7P-ID")) ||
            !string.IsNullOrWhiteSpace(GetProperty(proxyEvent, "MID")));
        if (!hasResponseCorrelation && !hasEventCorrelation)
        {
            errors.Add("no response or proxy-event correlation ID was found");
        }

        return errors;
    }

    private static IReadOnlyDictionary<string, string> ReadResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }
        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }
        return headers;
    }

    private static IReadOnlyList<JsonElement> ReadEvents(
        string eventLogPath,
        string? s7pId,
        string scenarioName)
    {
        if (!File.Exists(eventLogPath))
        {
            return [];
        }

        var events = new List<JsonElement>();
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
                var mid = GetProperty(root, "MID");
                var eventS7pId = GetProperty(root, "S7P-ID");
                var path = GetProperty(root, "Path");
                var matchesId = !string.IsNullOrWhiteSpace(s7pId) &&
                    (string.Equals(eventS7pId, s7pId, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mid, s7pId, StringComparison.OrdinalIgnoreCase) ||
                     mid?.StartsWith(s7pId + "-", StringComparison.OrdinalIgnoreCase) == true);
                var matchesPath = path?.Contains(scenarioName, StringComparison.OrdinalIgnoreCase) == true;
                if (matchesId || matchesPath)
                {
                    events.Add(root.Clone());
                }
            }
            catch (JsonException)
            {
                // Preserve malformed lines in the artifact; they are not evidence for this request.
            }
        }

        return events;
    }

    private static string BuildCombinedBackendLog(
        IReadOnlyDictionary<string, string> responseHeaders,
        IReadOnlyList<JsonElement> events)
    {
        var logs = new List<string>();
        var responseLog = FindHeader(responseHeaders, "backendLog");
        if (!string.IsNullOrWhiteSpace(responseLog))
        {
            logs.Add(responseLog);
        }

        foreach (var proxyEvent in events)
        {
            foreach (var property in proxyEvent.EnumerateObject())
            {
                if (property.Name.EndsWith("backendLog", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        logs.Add(value);
                    }
                }
            }
        }

        return string.Join(" | ", logs.Distinct(StringComparer.Ordinal));
    }

    private static string? FindEvidenceValue(
        IReadOnlyDictionary<string, string> responseHeaders,
        IReadOnlyList<JsonElement> events,
        string headerName)
    {
        var direct = FindHeader(responseHeaders, headerName);
        if (!string.IsNullOrWhiteSpace(direct) &&
            !string.Equals(direct, "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return direct;
        }

        foreach (var proxyEvent in events.Reverse())
        {
            foreach (var property in proxyEvent.EnumerateObject())
            {
                if ((string.Equals(property.Name, headerName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(property.Name, "Response-" + headerName, StringComparison.OrdinalIgnoreCase) ||
                     Regex.IsMatch(
                         property.Name,
                         "^Attempt-\\d+-" + Regex.Escape(headerName) + "$",
                         RegexOptions.IgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase))
                    {
                        return value;
                    }
                }
            }
        }

        return null;
    }

    private static string? FindHeader(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) ? value : null;

    private static string? GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }
        return null;
    }

    private static string EncodeSpec(ScenarioResponseSpec spec)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(spec, s_jsonOptions);
        return Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static async Task WaitUntilReadyAsync(
        LoggedProcess process,
        Uri endpoint,
        TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        while (stopwatch.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{process.Name} exited with code {process.ExitCode} before readiness." +
                    Environment.NewLine + process.GetLogTail());
            }

            try
            {
                using var response = await client.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(
            $"Timed out waiting for {endpoint}. Last result: {lastError ?? "none"}." +
            Environment.NewLine + process.GetLogTail());
    }

    private static async Task WarmSimulatorAsync(Uri endpoint, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                using var response = await client.GetAsync(endpoint);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Timed out warming simulator at {endpoint}. Last result: {lastError ?? "none"}.");
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string SanitizePathPart(string value)
        => Regex.Replace(value, "[^A-Za-z0-9_.-]", "-");

    private void AttachScenarioArtifacts(string artifactDirectory)
    {
        if (!Directory.Exists(artifactDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(artifactDirectory))
        {
            TestContext.AddResultFile(file);
        }
    }

    private sealed class LoggedProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdout;
        private readonly StreamWriter _stderr;
        private readonly Task _stdoutTask;
        private readonly Task _stderrTask;

        private LoggedProcess(
            string name,
            Process process,
            StreamWriter stdout,
            StreamWriter stderr,
            Task stdoutTask,
            Task stderrTask)
        {
            Name = name;
            _process = process;
            _stdout = stdout;
            _stderr = stderr;
            _stdoutTask = stdoutTask;
            _stderrTask = stderrTask;
        }

        public string Name { get; }
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

        public static LoggedProcess Start(
            string name,
            ProcessStartInfo startInfo,
            string stdoutPath,
            string stderrPath)
        {
            var process = Process.Start(startInfo) ??
                throw new InvalidOperationException($"Failed to start {name}.");
            var stdout = new StreamWriter(stdoutPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            var stderr = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            var stdoutTask = CopyOutputAsync(process.StandardOutput, stdout);
            var stderrTask = CopyOutputAsync(process.StandardError, stderr);
            return new LoggedProcess(name, process, stdout, stderr, stdoutTask, stderrTask);
        }

        public async Task StopAsync()
        {
            if (!_process.HasExited)
            {
                var gracefulStopRequested = false;
                if (OperatingSystem.IsWindows())
                {
                    gracefulStopRequested = _process.CloseMainWindow();
                }
                else
                {
                    using var signalProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/kill",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { "-TERM", _process.Id.ToString(CultureInfo.InvariantCulture) }
                    });
                    if (signalProcess != null)
                    {
                        await signalProcess.WaitForExitAsync();
                        gracefulStopRequested = signalProcess.ExitCode == 0;
                    }
                }

                if (gracefulStopRequested)
                {
                    using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        await _process.WaitForExitAsync(shutdownTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                else
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            await _process.WaitForExitAsync();
            await Task.WhenAll(_stdoutTask, _stderrTask);
            await _stdout.FlushAsync();
            await _stderr.FlushAsync();
        }

        public string GetLogTail()
        {
            var builder = new StringBuilder();
            foreach (var path in new[] { _stdout.BaseStream is FileStream output ? output.Name : null, _stderr.BaseStream is FileStream error ? error.Name : null })
            {
                if (path != null && File.Exists(path))
                {
                    builder.AppendLine(string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(30)));
                }
            }
            return builder.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _stdout.DisposeAsync();
            await _stderr.DisposeAsync();
            _process.Dispose();
        }

        private static async Task CopyOutputAsync(StreamReader source, StreamWriter destination)
        {
            while (await source.ReadLineAsync() is { } line)
            {
                await destination.WriteLineAsync(line);
            }
        }
    }

    private sealed class PolicyTestSettings
    {
        public required string ProxyRoutePrefix { get; init; }
        public required Uri SimulatorBaseAddress { get; init; }
        public int RequestTimeoutSeconds { get; init; }
        public bool KeepArtifacts { get; init; }
        public string? ScenarioName { get; init; }
        public required IReadOnlyDictionary<string, string> ProxyEnvironment { get; init; }

        public static PolicyTestSettings? FromEnvironment(IReadOnlyDictionary<string, string> proxyEnvironment)
        {
            var rawRoute = Environment.GetEnvironmentVariable("POLICY_TEST_APIM_URL");
            if (string.IsNullOrWhiteSpace(rawRoute))
            {
                return null;
            }

            if (Uri.TryCreate(rawRoute, UriKind.Absolute, out _) ||
                rawRoute.Contains('?') ||
                rawRoute.Contains('#'))
            {
                throw new InvalidOperationException(
                    "POLICY_TEST_APIM_URL must be a proxy-relative route such as policytest, not an absolute URL.");
            }

            var proxyRoutePrefix = "/" + rawRoute.Trim().Trim('/');
            if (proxyRoutePrefix == "/")
            {
                throw new InvalidOperationException("POLICY_TEST_APIM_URL cannot resolve to the proxy root.");
            }

            var rawSimulatorUrl = Environment.GetEnvironmentVariable("POLICY_TEST_SIMULATOR_URL");
            if (!Uri.TryCreate(rawSimulatorUrl, UriKind.Absolute, out var simulatorBaseAddress) ||
                (simulatorBaseAddress.Scheme != Uri.UriSchemeHttp &&
                 simulatorBaseAddress.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "POLICY_TEST_SIMULATOR_URL must be an absolute HTTP or HTTPS simulator origin.");
            }

            var timeout = int.TryParse(
                Environment.GetEnvironmentVariable("POLICY_TEST_TIMEOUT_SECONDS"),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedTimeout) && parsedTimeout > 0
                ? parsedTimeout
                : DefaultRequestTimeoutSeconds;

            return new PolicyTestSettings
            {
                ProxyRoutePrefix = proxyRoutePrefix,
                SimulatorBaseAddress = simulatorBaseAddress,
                RequestTimeoutSeconds = timeout,
                ScenarioName = Environment.GetEnvironmentVariable("POLICY_TEST_SCENARIO"),
                ProxyEnvironment = proxyEnvironment,
                KeepArtifacts = bool.TryParse(
                    Environment.GetEnvironmentVariable("POLICY_TEST_KEEP_ARTIFACTS"),
                    out var keepArtifacts) && keepArtifacts
            };
        }
    }

    private sealed class PolicyTestLocalConfig
    {
        public Dictionary<string, string> TestEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ProxyEnvironment { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public static PolicyTestLocalConfig? Load()
        {
            var basePath = Path.Combine(AppContext.BaseDirectory, "configs", "policy-test.json");
            if (!File.Exists(basePath))
            {
                throw new FileNotFoundException(
                    "Policy test base config was not found. Restore configs/policy-test.json.",
                    basePath);
            }

            var config = JsonSerializer.Deserialize<PolicyTestLocalConfig>(
                File.ReadAllText(basePath),
                s_jsonOptions) ?? new PolicyTestLocalConfig();
            var configuredPath = Environment.GetEnvironmentVariable("POLICY_TEST_CONFIG_PATH");
            var path = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(AppContext.BaseDirectory, "configs", "policy-test.local.json")
                : Path.GetFullPath(configuredPath);

            if (File.Exists(path))
            {
                var localConfig = JsonSerializer.Deserialize<PolicyTestLocalConfig>(
                    File.ReadAllText(path),
                    s_jsonOptions) ?? new PolicyTestLocalConfig();
                foreach (var setting in localConfig.TestEnvironment)
                {
                    config.TestEnvironment[setting.Key] = setting.Value;
                }
                foreach (var setting in localConfig.ProxyEnvironment)
                {
                    config.ProxyEnvironment[setting.Key] = setting.Value;
                }
            }

            foreach (var requiredKey in new[] { "EVENT_LOGGERS", "LogToEvents", "LogHeaders", "DependancyHeaders" })
            {
                if (!config.ProxyEnvironment.ContainsKey(requiredKey))
                {
                    throw new InvalidOperationException(
                        $"Merged policy test config is missing proxyEnvironment.{requiredKey}.");
                }
            }
            if (!config.ProxyEnvironment.TryGetValue("Host_apim", out var hostApim) ||
                string.IsNullOrWhiteSpace(hostApim))
            {
                return null;
            }
            return config;
        }

        public void ApplyTestEnvironmentDefaults()
        {
            foreach (var setting in TestEnvironment)
            {
                if (Environment.GetEnvironmentVariable(setting.Key) == null)
                {
                    Environment.SetEnvironmentVariable(setting.Key, setting.Value);
                }
            }
        }
    }

    private sealed class ScenarioCatalog
    {
        public List<ScenarioDefinition> Scenarios { get; init; } = [];
    }

    private sealed class ScenarioDefinition
    {
        public required string Name { get; init; }
        public required string Model { get; init; }
        public required string PriorityKey { get; init; }
        public required ScenarioResponseSpec A { get; init; }
        public required ScenarioResponseSpec B { get; init; }
        public int? TtlSeconds { get; init; }
        public required ScenarioExpectation Expected { get; init; }
    }

    private sealed class ScenarioResponseSpec
    {
        public int DelayMs { get; init; }
        public int Status { get; init; } = 200;
        public string Body { get; init; } = "openai";
        public string? BodyText { get; init; }
        public Dictionary<string, JsonElement>? Headers { get; init; }
    }

    private sealed class ScenarioExpectation
    {
        public int Status { get; init; }
        public int? BackendAttempts { get; init; }
        public List<string> RequiredLog { get; init; } = [];
        public List<string> ForbiddenLog { get; init; } = [];
    }

    private sealed record ScenarioResult(
        string ArtifactDirectory,
        int StatusCode,
        string? BackendAttempts,
        IReadOnlyList<string> Errors);
}
