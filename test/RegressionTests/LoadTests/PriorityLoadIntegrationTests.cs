using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Runs the proxy against the Python null server and verifies the built-in request priorities under load.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PriorityLoadIntegrationTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["priority-load"] = new(
                "Reliability & Capacity",
                "Priority processing under load",
                "Confirms high, medium, and low priority traffic remains serviceable and observable under concurrent load.")
        };

    private const int RequestCount = 1000;
    private const int ProcessTimeoutSeconds = 120;
    private const int StartupTimeoutSeconds = 180;
    private static readonly (string Key, int Value)[] s_priorities =
    [
        ("high", 1),
        ("medium", 2),
        ("low", 3),
    ];

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts three processes and reports latency and outcome statistics for each built-in priority.
    /// </summary>
    [TestMethod]
    [RegressionTestCase(
        "priority-load",
        "All priority classes complete under load",
        "Runs 1,000 concurrent requests and requires every high, medium, and low priority request to complete successfully.")]
    [TestCategory("Integration")]
    [TestCategory("Load")]
    [Timeout(300_000)]
    public async Task BuiltInPriorities_ProcessOneThousandConcurrentCurlRequests()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The priority load harness requires Linux or WSL with python3 and curl available on PATH.");
        }

        var nullServerScript = Path.Combine(AppContext.BaseDirectory, "tools", "stream_server.py");
        var proxyAssembly = Path.Combine(AppContext.BaseDirectory, "SimpleL7Proxy.dll");
        Assert.IsTrue(File.Exists(nullServerScript), $"Null server script was not copied to {nullServerScript}.");
        Assert.IsTrue(File.Exists(proxyAssembly), $"Proxy assembly was not found at {proxyAssembly}.");

        var artifactDirectory = Path.Combine(
            Path.GetTempPath(),
            $"simplel7proxy-priority-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);

        try
        {
            var result = await RunHarnessAsync(nullServerScript, proxyAssembly, artifactDirectory);
            var table = FormatStatisticsTable(result);
            TestContext.WriteLine(table);

            var reportPath = Environment.GetEnvironmentVariable("PRIORITY_LOAD_REPORT_PATH");
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                reportPath = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(
                    reportPath,
                    table,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            Assert.AreEqual(0, result.CurlExitCode, $"curl failed. See {result.CurlErrorLogPath}.");
            Assert.AreEqual(0, result.MalformedLineCount, "Every curl result must be parseable.");
            Assert.AreEqual(RequestCount, result.Records.Count, "Every configured curl transfer must produce a result.");

            foreach (var priority in s_priorities)
            {
                var expectedCount = result.ExpectedCounts[priority.Key];
                var priorityRecords = result.Records
                    .Where(record => record.Priority == priority.Key)
                    .ToArray();

                Assert.AreEqual(
                    expectedCount,
                    priorityRecords.Length,
                    $"Priority '{priority.Key}' did not complete every configured transfer.");
                Assert.IsTrue(
                    priorityRecords.All(record => record.ExitCode == 0 && record.StatusCode is >= 200 and < 300),
                    $"Priority '{priority.Key}' contains failed requests.");
            }

            AttachArtifacts(artifactDirectory);
        }
        catch
        {
            TestContext.WriteLine($"Priority load artifacts retained at {artifactDirectory}");
            AttachArtifacts(artifactDirectory);
            throw;
        }
    }

    private async Task<HarnessResult> RunHarnessAsync(
        string nullServerScript,
        string proxyAssembly,
        string artifactDirectory)
    {
        var nullServerPort = GetAvailablePort();
        var proxyPort = GetAvailablePort();
        while (proxyPort == nullServerPort)
        {
            proxyPort = GetAvailablePort();
        }

        var nullServerOutput = Path.Combine(artifactDirectory, "null-server.stdout.log");
        var nullServerError = Path.Combine(artifactDirectory, "null-server.stderr.log");
        var proxyOutput = Path.Combine(artifactDirectory, "proxy.stdout.log");
        var proxyError = Path.Combine(artifactDirectory, "proxy.stderr.log");
        var eventLog = Path.Combine(artifactDirectory, "events.ndjson");
        var curlOutput = Path.Combine(artifactDirectory, "curl-results.tsv");
        var curlError = Path.Combine(artifactDirectory, "curl.stderr.log");
        var curlConfig = Path.Combine(artifactDirectory, "curl.cfg");

        var (plan, expectedCounts) = CreateRequestPlan(RequestCount);
        WriteCurlConfig(curlConfig, proxyPort, plan);

        TestContext.WriteLine($"Null server: http://127.0.0.1:{nullServerPort}");
        TestContext.WriteLine($"Proxy:       http://127.0.0.1:{proxyPort}");
        TestContext.WriteLine(
            $"export Host1='host=http://127.0.0.1:{nullServerPort};mode=direct' Port={proxyPort}");
        TestContext.WriteLine("S7PPriorityKey mapping: high=1, medium=2, low=3");

        var nullServerStartInfo = CreateStartInfo(
            "python3",
            ["-u", nullServerScript, "--port", nullServerPort.ToString(CultureInfo.InvariantCulture)],
            Path.GetDirectoryName(nullServerScript)!);

        await using var nullServer = LoggedProcess.Start(
            "null server",
            nullServerStartInfo,
            nullServerOutput,
            nullServerError);

        await WaitUntilReadyAsync(
            nullServer,
            new Uri($"http://127.0.0.1:{nullServerPort}/health"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));

        var proxyStartInfo = CreateStartInfo(
            "dotnet",
            [proxyAssembly],
            AppContext.BaseDirectory);
        ConfigureProxyEnvironment(proxyStartInfo, nullServerPort, proxyPort, eventLog);

        await using var proxy = LoggedProcess.Start(
            "proxy",
            proxyStartInfo,
            proxyOutput,
            proxyError);

        await WaitUntilReadyAsync(
            proxy,
            new Uri($"http://127.0.0.1:{proxyPort}/readiness"),
            TimeSpan.FromSeconds(StartupTimeoutSeconds));

        var curlStartInfo = CreateStartInfo(
            "curl",
            [
                "--silent",
                "--show-error",
                "--parallel",
                "--parallel-immediate",
                "--parallel-max",
                RequestCount.ToString(CultureInfo.InvariantCulture),
                "--config",
                curlConfig,
            ],
            artifactDirectory);

        TestContext.WriteLine($"Starting one curl process with {RequestCount} parallel transfers.");
        var stopwatch = Stopwatch.StartNew();
        await using var curl = LoggedProcess.Start(
            "curl load",
            curlStartInfo,
            curlOutput,
            curlError);
        using var curlTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(ProcessTimeoutSeconds + 30));
        var curlExitCode = await curl.WaitForExitAsync(curlTimeout.Token);
        stopwatch.Stop();

        var (records, malformedLineCount) = ParseCurlResults(curlOutput);
        return new HarnessResult(
            records,
            expectedCounts,
            malformedLineCount,
            curlExitCode,
            stopwatch.Elapsed,
            curlError);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyCollection<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void ConfigureProxyEnvironment(
        ProcessStartInfo startInfo,
        int nullServerPort,
        int proxyPort,
        string eventLogPath)
    {
        foreach (var key in startInfo.Environment.Keys.ToArray())
        {
            if (Regex.IsMatch(
                key,
                "^(Host|Probe_path|IP)(\\d.*|[-_].*)$",
                RegexOptions.IgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        startInfo.Environment.Remove("AZURE_APPCONFIG_CONNECTION_STRING");
        startInfo.Environment.Remove("AZURE_APPCONFIG_ENDPOINT");
        startInfo.Environment.Remove("AZURE_APPCONFIG_LABEL");
        startInfo.Environment.Remove("APPENDHOSTSFILE");
        startInfo.Environment.Remove("AppendHostsFile");
        startInfo.Environment["Host1"] = $"host=http://127.0.0.1:{nullServerPort};mode=direct";
        startInfo.Environment["Port"] = proxyPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["EVENT_LOGGERS"] = "file";
        startInfo.Environment["LOGFILE_NAME"] = eventLogPath;
        startInfo.Environment["LOG_LEVEL"] = "Warning";
        startInfo.Environment["APPINSIGHTS_CONNECTIONSTRING"] = string.Empty;
        startInfo.Environment["AsyncModeEnabled"] = "false";
        startInfo.Environment["UseProfiles"] = "false";
        startInfo.Environment["UserConfigRequired"] = "false";
        startInfo.Environment["ValidateAuthAppID"] = "false";
        startInfo.Environment["ValidateAuthConfig"] = "enabled=false, mode=none, header=S7P-KEY";
        startInfo.Environment["MaxQueueLength"] = "2000";
        startInfo.Environment["Workers"] = "10";
        startInfo.Environment["Timeout"] = (ProcessTimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["PriorityKeyHeader"] = "S7PPriorityKey";
        startInfo.Environment["PriorityKeys"] = "high,medium,low";
        startInfo.Environment["PriorityValues"] = "1,2,3";
        startInfo.Environment["PriorityWorkers"] = "2:1,3:1";
    }

    private static (IReadOnlyList<RequestPlanItem> Plan, IReadOnlyDictionary<string, int> ExpectedCounts)
        CreateRequestPlan(int count)
    {
        var plan = Enumerable.Range(0, count)
            .Select(index => new RequestPlanItem(index + 1, s_priorities[index % s_priorities.Length].Key))
            .ToArray();
        var expectedCounts = plan
            .GroupBy(item => item.Priority, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return (plan, expectedCounts);
    }

    private static void WriteCurlConfig(
        string path,
        int proxyPort,
        IReadOnlyList<RequestPlanItem> plan)
    {
        const string writeOut = @"%{url_effective}\t%{http_code}\t%{time_total}\t%{exitcode}\n";
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        for (var index = 0; index < plan.Count; index++)
        {
            var item = plan[index];
            writer.WriteLine(
                $"url = \"http://127.0.0.1:{proxyPort}/echo/resource?priority={item.Priority}&request={item.RequestId}\"");
            writer.WriteLine($"header = \"S7PPriorityKey: {item.Priority}\"");
            writer.WriteLine("output = \"/dev/null\"");
            writer.WriteLine("connect-timeout = 10");
            writer.WriteLine($"max-time = {ProcessTimeoutSeconds}");
            writer.WriteLine($"write-out = \"{writeOut}\"");
            if (index < plan.Count - 1)
            {
                writer.WriteLine("next");
            }
        }
    }

    private static async Task WaitUntilReadyAsync(
        LoggedProcess process,
        Uri endpoint,
        TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var deadline = Stopwatch.StartNew();
        string? lastError = null;

        while (deadline.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{process.Name} exited with code {process.ExitCode} before {endpoint} became ready."
                    + Environment.NewLine
                    + process.GetLogTail());
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

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException(
            $"Timed out waiting for {process.Name} at {endpoint}. Last result: {lastError ?? "no response"}."
            + Environment.NewLine
            + process.GetLogTail());
    }

    private static (IReadOnlyList<CurlRecord> Records, int MalformedLineCount) ParseCurlResults(string path)
    {
        var records = new List<CurlRecord>(RequestCount);
        var malformedLineCount = 0;

        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('\t');
            if (fields.Length != 4
                || !Uri.TryCreate(fields[0], UriKind.Absolute, out var uri)
                || !TryReadQueryValue(uri, "priority", out var priority)
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode)
                || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds)
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var exitCode))
            {
                malformedLineCount++;
                continue;
            }

            records.Add(new CurlRecord(priority, statusCode, durationSeconds * 1000, exitCode));
        }

        return (records, malformedLineCount);
    }

    private static bool TryReadQueryValue(Uri uri, string key, out string value)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0
                || !pair.AsSpan(0, separator).Equals(key, StringComparison.Ordinal))
            {
                continue;
            }

            value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string FormatStatisticsTable(HarnessResult result)
    {
        var headers = new[]
        {
            "Priority",
            "Value",
            "Sent",
            "Done",
            "2xx",
            "Failed",
            "Avg ms",
            "P50 ms",
            "P95 ms",
            "Max ms",
            "Req/s",
            "Outcomes",
        };
        var rows = new List<string[]>();

        foreach (var priority in s_priorities)
        {
            var records = result.Records
                .Where(record => record.Priority == priority.Key)
                .ToArray();
            rows.Add(CreateStatisticsRow(
                priority.Key,
                priority.Value.ToString(CultureInfo.InvariantCulture),
                result.ExpectedCounts[priority.Key],
                records,
                result.Elapsed));
        }

        rows.Add(CreateStatisticsRow(
            "ALL",
            "-",
            RequestCount,
            result.Records,
            result.Elapsed));

        var widths = headers.Select(header => header.Length).ToArray();
        foreach (var row in rows)
        {
            for (var index = 0; index < row.Length; index++)
            {
                widths[index] = Math.Max(widths[index], row[index].Length);
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine(FormatTableRow(headers, widths));
        builder.AppendLine(string.Join("  ", widths.Select(width => new string('-', width))));
        foreach (var row in rows)
        {
            builder.AppendLine(FormatTableRow(row, widths));
        }

        builder.Append("Wall time: ")
            .Append(result.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture))
            .AppendLine(" seconds");
        return builder.ToString();
    }

    private static string[] CreateStatisticsRow(
        string name,
        string value,
        int sent,
        IReadOnlyCollection<CurlRecord> records,
        TimeSpan elapsed)
    {
        var durations = records.Select(record => record.DurationMilliseconds).Order().ToArray();
        var successful = records.Count(
            record => record.ExitCode == 0 && record.StatusCode is >= 200 and < 300);
        var outcomes = records
            .GroupBy(
                record => record.ExitCode == 0
                    ? record.StatusCode.ToString(CultureInfo.InvariantCulture)
                    : $"curl-{record.ExitCode}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToList();
        if (records.Count < sent)
        {
            outcomes.Add($"missing:{sent - records.Count}");
        }

        return
        [
            name,
            value,
            sent.ToString(CultureInfo.InvariantCulture),
            records.Count.ToString(CultureInfo.InvariantCulture),
            successful.ToString(CultureInfo.InvariantCulture),
            (sent - successful).ToString(CultureInfo.InvariantCulture),
            durations.Length == 0 ? "0.00" : durations.Average().ToString("F2", CultureInfo.InvariantCulture),
            Percentile(durations, 0.50).ToString("F2", CultureInfo.InvariantCulture),
            Percentile(durations, 0.95).ToString("F2", CultureInfo.InvariantCulture),
            (durations.Length == 0 ? 0 : durations[^1]).ToString("F2", CultureInfo.InvariantCulture),
            (sent / Math.Max(elapsed.TotalSeconds, 0.001)).ToString("F2", CultureInfo.InvariantCulture),
            outcomes.Count == 0 ? "-" : string.Join(',', outcomes),
        ];
    }

    private static string FormatTableRow(IReadOnlyList<string> values, IReadOnlyList<int> widths)
    {
        return string.Join(
            "  ",
            values.Select((value, index) => index is 0 or 11
                ? value.PadRight(widths[index])
                : value.PadLeft(widths[index])));
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var position = (sortedValues.Count - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return sortedValues[lowerIndex];
        }

        return sortedValues[lowerIndex] * (upperIndex - position)
            + sortedValues[upperIndex] * (position - lowerIndex);
    }

    private void AttachArtifacts(string artifactDirectory)
    {
        if (!Directory.Exists(artifactDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(artifactDirectory))
        {
            TestContext.AddResultFile(path);
        }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class LoggedProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly FileStream _standardOutput;
        private readonly FileStream _standardError;
        private readonly Task _standardOutputPump;
        private readonly Task _standardErrorPump;
        private readonly string _standardOutputPath;
        private readonly string _standardErrorPath;

        private LoggedProcess(
            string name,
            Process process,
            FileStream standardOutput,
            FileStream standardError,
            string standardOutputPath,
            string standardErrorPath)
        {
            Name = name;
            _process = process;
            _standardOutput = standardOutput;
            _standardError = standardError;
            _standardOutputPath = standardOutputPath;
            _standardErrorPath = standardErrorPath;
            _standardOutputPump = process.StandardOutput.BaseStream.CopyToAsync(standardOutput);
            _standardErrorPump = process.StandardError.BaseStream.CopyToAsync(standardError);
        }

        public string Name { get; }
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public static LoggedProcess Start(
            string name,
            ProcessStartInfo startInfo,
            string standardOutputPath,
            string standardErrorPath)
        {
            var standardOutput = OpenLog(standardOutputPath);
            var standardError = OpenLog(standardErrorPath);
            try
            {
                var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"Failed to start {name}.");
                return new LoggedProcess(
                    name,
                    process,
                    standardOutput,
                    standardError,
                    standardOutputPath,
                    standardErrorPath);
            }
            catch
            {
                standardOutput.Dispose();
                standardError.Dispose();
                throw;
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(_standardOutputPump, _standardErrorPump);
            await _standardOutput.FlushAsync(cancellationToken);
            await _standardError.FlushAsync(cancellationToken);
            return _process.ExitCode;
        }

        public string GetLogTail()
        {
            return $"--- {Name} stdout ---{Environment.NewLine}{ReadTail(_standardOutputPath)}"
                + Environment.NewLine
                + $"--- {Name} stderr ---{Environment.NewLine}{ReadTail(_standardErrorPath)}";
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            try
            {
                using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _process.WaitForExitAsync(shutdownTimeout.Token);
                await Task.WhenAll(_standardOutputPump, _standardErrorPump);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await _standardOutput.DisposeAsync();
                await _standardError.DisposeAsync();
                _process.Dispose();
            }
        }

        private static FileStream OpenLog(string path)
        {
            return new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
        }

        private static string ReadTail(string path)
        {
            try
            {
                return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(30));
            }
            catch (IOException exception)
            {
                return $"Log unavailable: {exception.Message}";
            }
        }
    }

    private sealed record RequestPlanItem(int RequestId, string Priority);
    private sealed record CurlRecord(string Priority, int StatusCode, double DurationMilliseconds, int ExitCode);
    private sealed record HarnessResult(
        IReadOnlyList<CurlRecord> Records,
        IReadOnlyDictionary<string, int> ExpectedCounts,
        int MalformedLineCount,
        int CurlExitCode,
        TimeSpan Elapsed,
        string CurlErrorLogPath);
}