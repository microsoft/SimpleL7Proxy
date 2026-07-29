using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace RegressionReportRenderer;

internal static class Program
{
    private const string MetadataInterfaceName = "SimpleL7Proxy.Test.IRegressionTestMetadata";
    private const string MetadataAttributeName = "SimpleL7Proxy.Test.RegressionTestCaseAttribute";
    private const string TestClassAttributeName = "Microsoft.VisualStudio.TestTools.UnitTesting.TestClassAttribute";
    private const string TestMethodAttributeName = "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute";
    private const string DataTestMethodAttributeName = "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute";
    private static readonly XNamespace TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            var options = CommandOptions.Parse(args);
            Render(options);
            Console.WriteLine(options.HtmlPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Regression report generation failed: {exception.Message}");
            return 1;
        }
    }

    private static void Render(CommandOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.ManifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(options.HtmlPath)!);

        var catalog = MetadataCatalog.Load(options.TestAssemblyPath);
        var execution = new ExecutionRecord
        {
            Id = options.ExecutionId,
            Label = options.Label,
            Command = options.Command,
            StartedUtc = options.StartedUtc,
            CompletedUtc = options.CompletedUtc,
            ExitCode = options.ExitCode,
            TrxPath = File.Exists(options.TrxPath)
                ? Path.GetRelativePath(Path.GetDirectoryName(options.HtmlPath)!, options.TrxPath)
                : string.Empty,
            ConsoleLog = File.Exists(options.ConsoleLogPath)
                ? Path.GetRelativePath(Path.GetDirectoryName(options.HtmlPath)!, options.ConsoleLogPath)
                : string.Empty,
            ConsoleTail = ReadConsoleTail(options.ConsoleLogPath)
        };

        if (File.Exists(options.TrxPath))
        {
            ParseTrx(options.TrxPath, catalog, execution);
        }
        else
        {
            execution.ParseError = $"TRX file was not created: {options.TrxPath}";
        }
        execution.Summary = Summary.From(execution.Tests);

        var lockPath = options.ManifestPath + ".lock";
        using var lockStream = AcquireLock(lockPath, TimeSpan.FromSeconds(30));
        try
        {
            var manifest = File.Exists(options.ManifestPath)
                ? JsonSerializer.Deserialize<ReportManifest>(File.ReadAllText(options.ManifestPath), JsonOptions) ?? new ReportManifest()
                : new ReportManifest
                {
                    MasterRunId = options.MasterRunId,
                    CreatedUtc = options.StartedUtc
                };

            if (!string.IsNullOrEmpty(manifest.MasterRunId) &&
                !string.Equals(manifest.MasterRunId, options.MasterRunId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Manifest master run '{manifest.MasterRunId}' does not match '{options.MasterRunId}'.");
            }

            manifest.SchemaVersion = 3;
            manifest.MasterRunId = options.MasterRunId;
            manifest.UpdatedUtc = UtcNow();
            foreach (var existingExecution in manifest.Executions)
            {
                foreach (var test in existingExecution.Tests)
                {
                    catalog.Apply(test);
                }
            }

            manifest.Executions.RemoveAll(item => string.Equals(item.Id, options.ExecutionId, StringComparison.Ordinal));
            manifest.Executions.Add(execution);
            manifest.Executions.Sort((left, right) =>
            {
                var started = string.Compare(left.StartedUtc, right.StartedUtc, StringComparison.Ordinal);
                return started != 0 ? started : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });

            WriteAtomic(options.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
            WriteAtomic(options.HtmlPath, HtmlReport.Render(manifest));
        }
        finally
        {
            lockStream.Dispose();
            File.Delete(lockPath);
        }

        WriteLandingPage(options, catalog);
    }

    private static void WriteLandingPage(CommandOptions options, MetadataCatalog catalog)
    {
        Directory.CreateDirectory(options.HistoryRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(options.LandingPath)!);

        var entries = new List<HistoryEntry>();
        foreach (var directory in Directory.EnumerateDirectories(options.HistoryRootPath))
        {
            var manifestPath = Path.Combine(directory, "results.json");
            var reportPath = Path.Combine(directory, "index.html");
            if (!File.Exists(manifestPath) || !File.Exists(reportPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize<ReportManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest == null) continue;
                foreach (var execution in manifest.Executions)
                {
                    foreach (var test in execution.Tests)
                    {
                        catalog.Apply(test);
                    }
                }
                entries.Add(new HistoryEntry(
                    Path.GetFileName(directory),
                    Path.GetRelativePath(Path.GetDirectoryName(options.LandingPath)!, reportPath).Replace('\\', '/'),
                    manifest));
            }
            catch (JsonException)
            {
                // Ignore a history entry that is still being written by another execution.
            }
        }

        entries.Sort((left, right) => string.Compare(right.FolderName, left.FolderName, StringComparison.Ordinal));
        var lockPath = options.LandingPath + ".lock";
        using var lockStream = AcquireLock(lockPath, TimeSpan.FromSeconds(30));
        try
        {
            WriteAtomic(options.LandingPath, LandingReport.Render(entries, catalog.Domains));
        }
        finally
        {
            lockStream.Dispose();
            File.Delete(lockPath);
        }
    }

    private static void ParseTrx(string path, MetadataCatalog catalog, ExecutionRecord execution)
    {
        var document = XDocument.Load(path);
        var root = document.Root ?? throw new InvalidDataException("TRX has no root element.");
        var definitions = root
            .Descendants(TrxNamespace + "UnitTest")
            .Where(element => element.Parent?.Name == TrxNamespace + "TestDefinitions")
            .ToDictionary(
                element => Attribute(element, "id"),
                element =>
                {
                    var method = element.Element(TrxNamespace + "TestMethod");
                    return new TrxDefinition
                    {
                        Name = Attribute(element, "name"),
                        ClassName = Attribute(method, "className"),
                        MethodName = Attribute(method, "name"),
                        Categories = element
                            .Descendants(TrxNamespace + "TestCategoryItem")
                            .Select(item => Attribute(item, "TestCategory"))
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .ToList()
                    };
                });

        foreach (var result in root.Descendants(TrxNamespace + "UnitTestResult"))
        {
            definitions.TryGetValue(Attribute(result, "testId"), out var definition);
            definition ??= new TrxDefinition();
            var output = result.Element(TrxNamespace + "Output");
            var record = new TestRecord
            {
                Name = Attribute(result, "testName", definition.MethodName),
                DisplayName = Attribute(result, "testName", definition.Name),
                DefinitionName = definition.Name,
                MethodName = definition.MethodName,
                ClassName = definition.ClassName,
                Categories = definition.Categories,
                Outcome = Attribute(result, "outcome", "Unknown"),
                DurationMs = ParseDurationMs(Attribute(result, "duration")),
                StartTime = Attribute(result, "startTime"),
                EndTime = Attribute(result, "endTime"),
                Stdout = ElementText(output, "StdOut"),
                Stderr = ElementText(output, "StdErr"),
                ErrorMessage = ElementText(output?.Element(TrxNamespace + "ErrorInfo"), "Message"),
                StackTrace = ElementText(output?.Element(TrxNamespace + "ErrorInfo"), "StackTrace")
            };
            catalog.Apply(record);
            execution.Tests.Add(record);
        }

        var times = root.Element(TrxNamespace + "Times");
        execution.TrxRunId = Attribute(root, "id");
        execution.TrxRunName = Attribute(root, "name");
        execution.TrxStartedUtc = Attribute(times, "start");
        execution.TrxCompletedUtc = Attribute(times, "finish");
    }

    private static FileStream AcquireLock(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string ReadConsoleTail(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(200));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string Attribute(XElement? element, string name, string fallback = "")
        => element?.Attribute(name)?.Value ?? fallback;

    private static string ElementText(XElement? parent, string name)
        => parent?.Element(TrxNamespace + name)?.Value ?? string.Empty;

    private static double ParseDurationMs(string value)
        => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration)
            ? Math.Round(duration.TotalMilliseconds, 3)
            : 0;

    private static string UtcNow()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

internal sealed class MetadataCatalog
{
    private readonly Dictionary<(string ClassName, string MethodName), TestMetadata> _tests;
    public IReadOnlyList<string> Domains { get; }

    private MetadataCatalog(Dictionary<(string, string), TestMetadata> tests)
    {
        _tests = tests;
        Domains = tests.Values
            .Select(metadata => metadata.Feature.Domain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToList();
    }

    public static MetadataCatalog Load(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        var metadataInterface = assembly.GetType("SimpleL7Proxy.Test.IRegressionTestMetadata")
            ?? throw new InvalidOperationException($"{assemblyPath} does not define {"SimpleL7Proxy.Test.IRegressionTestMetadata"}.");
        var tests = new Dictionary<(string, string), TestMetadata>();
        var errors = new List<string>();

        foreach (var type in assembly.GetTypes().Where(HasTestClassAttribute))
        {
            if (!metadataInterface.IsAssignableFrom(type))
            {
                errors.Add($"{type.FullName} does not implement {metadataInterface.Name}.");
                continue;
            }

            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Could not create metadata provider {type.FullName}.");
            var features = ReadFeatures(type, instance);

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(IsTestMethod))
            {
                var attribute = method.GetCustomAttributes(false)
                    .SingleOrDefault(item => item.GetType().FullName == "SimpleL7Proxy.Test.RegressionTestCaseAttribute");
                if (attribute == null)
                {
                    errors.Add($"{type.FullName}.{method.Name} is missing RegressionTestCaseAttribute.");
                    continue;
                }

                var featureKey = ReadStringProperty(attribute, "Feature");
                if (!features.TryGetValue(featureKey, out var feature))
                {
                    errors.Add($"{type.FullName}.{method.Name} references unknown feature '{featureKey}'.");
                    continue;
                }

                var title = ReadStringProperty(attribute, "Title");
                var description = ReadStringProperty(attribute, "Description");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                {
                    errors.Add($"{type.FullName}.{method.Name} must define a title and description.");
                    continue;
                }

                tests[(type.FullName!, method.Name)] = new TestMetadata(feature, title, description);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Regression metadata validation failed:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => "- " + error)));
        }

        return new MetadataCatalog(tests);
    }

    public void Apply(TestRecord test)
    {
        var methodName = DataRowName.Parse(test.Name).MethodName;
        if (!_tests.TryGetValue((test.ClassName, methodName), out var metadata))
        {
            throw new InvalidOperationException($"No regression metadata found for {test.ClassName}.{methodName}.");
        }

        var parsedName = DataRowName.Parse(test.Name);
        test.MethodName = methodName;
        test.Domain = metadata.Feature.Domain;
        test.Feature = metadata.Feature.Name;
        test.Why = metadata.Feature.WhyItMatters;
        test.Title = ExpandTemplate(metadata.Title, parsedName.Arguments);
        test.Description = ExpandTemplate(metadata.Description, parsedName.Arguments);
        if (!string.IsNullOrWhiteSpace(parsedName.RawArguments) &&
            !test.Description.Contains("Inputs:", StringComparison.Ordinal))
        {
            test.Description += $" Inputs: {parsedName.RawArguments}.";
        }
    }

    private static Dictionary<string, FeatureMetadata> ReadFeatures(Type type, object instance)
    {
        var property = type.GetProperty("RegressionFeatures", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{type.FullName} does not expose RegressionFeatures.");
        var value = property.GetValue(instance) as IEnumerable
            ?? throw new InvalidOperationException($"{type.FullName}.RegressionFeatures is not enumerable.");
        var features = new Dictionary<string, FeatureMetadata>(StringComparer.Ordinal);
        foreach (var item in value)
        {
            if (item == null) continue;
            var itemType = item.GetType();
            var key = itemType.GetProperty("Key")?.GetValue(item)?.ToString() ?? string.Empty;
            var feature = itemType.GetProperty("Value")?.GetValue(item)
                ?? throw new InvalidOperationException($"{type.FullName} contains a null feature.");
            features[key] = new FeatureMetadata(
                ReadStringProperty(feature, "Domain"),
                ReadStringProperty(feature, "Name"),
                ReadStringProperty(feature, "WhyItMatters"));
        }
        return features;
    }

    private static bool HasTestClassAttribute(Type type)
        => type.GetCustomAttributes(false).Any(attribute =>
            attribute.GetType().FullName == "Microsoft.VisualStudio.TestTools.UnitTesting.TestClassAttribute");

    private static bool IsTestMethod(MethodInfo method)
        => method.GetCustomAttributes(false).Any(attribute =>
            attribute.GetType().FullName is
                "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute" or
                "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute");

    private static string ReadStringProperty(object value, string property)
        => value.GetType().GetProperty(property)?.GetValue(value)?.ToString() ?? string.Empty;

    private static string ExpandTemplate(string template, IReadOnlyList<string> arguments)
    {
        var result = template;
        for (var index = 0; index < arguments.Count; index++)
        {
            result = result.Replace($"{{{index}}}", arguments[index], StringComparison.Ordinal);
        }
        return result;
    }

    private sealed record TestMetadata(FeatureMetadata Feature, string Title, string Description);
    private sealed record FeatureMetadata(string Domain, string Name, string WhyItMatters);
}

internal sealed record DataRowName(string MethodName, string RawArguments, IReadOnlyList<string> Arguments)
{
    public static DataRowName Parse(string displayName)
    {
        var marker = displayName.IndexOf(" (", StringComparison.Ordinal);
        if (marker <= 0 || !displayName.EndsWith(')'))
        {
            return new DataRowName(displayName, string.Empty, []);
        }

        var raw = displayName[(marker + 2)..^1];
        return new DataRowName(displayName[..marker], raw, ParseCsv(raw));
    }

    private static IReadOnlyList<string> ParseCsv(string value)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        values.Add(current.ToString().Trim());
        return values;
    }
}

internal static class HtmlReport
{
    public static string Render(ReportManifest manifest)
    {
        var tests = manifest.Executions
            .SelectMany(execution => execution.Tests.Select(test =>
            {
                test.ExecutionLabel = execution.Label;
                return test;
            }))
            .OrderBy(test => test.Domain, StringComparer.Ordinal)
            .ThenBy(test => test.Feature, StringComparer.Ordinal)
            .ThenBy(test => OutcomeRank(test.Outcome))
            .ThenBy(test => test.Title, StringComparer.Ordinal)
            .ThenBy(test => test.Name, StringComparer.Ordinal)
            .ToList();

        var total = tests.Count;
        var passed = tests.Count(test => test.Outcome == "Passed");
        var failed = tests.Count(test => OutcomeClass(test.Outcome) == "failed");
        var other = total - passed - failed;
        var overallFailed = failed > 0 || manifest.Executions.Any(execution =>
            execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError));

        var featureOptions = tests
            .Select(test => (Key: HierarchyKey(test), Label: $"{test.Domain} / {test.Feature}"))
            .Distinct()
            .OrderBy(item => item.Label, StringComparer.Ordinal)
            .Select(item => $"<option value=\"{Encode(item.Key)}\">{Encode(item.Label)}</option>");

        return Template
            .Replace("%%PAGE_TITLE%%", Encode($"Regression results: {manifest.MasterRunId}"), StringComparison.Ordinal)
            .Replace("%%MASTER_RUN_ID%%", Encode(manifest.MasterRunId), StringComparison.Ordinal)
            .Replace("%%EXECUTION_SUMMARY%%", Encode($"{manifest.Executions.Count} executions - Combined test time {FormatDuration(tests.Sum(test => test.DurationMs))} - Updated {manifest.UpdatedUtc}"), StringComparison.Ordinal)
            .Replace("%%OVERALL_CLASS%%", overallFailed ? "failed" : "passed", StringComparison.Ordinal)
            .Replace("%%OVERALL_STATUS%%", overallFailed ? "FAILED" : "PASSED", StringComparison.Ordinal)
            .Replace("%%TOTAL%%", total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%PASSED%%", passed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%FAILED%%", failed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%OTHER%%", other.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%%FEATURE_OPTIONS%%", string.Concat(featureOptions), StringComparison.Ordinal)
            .Replace("%%HIERARCHY%%", RenderHierarchy(tests), StringComparison.Ordinal)
            .Replace("%%DIAGNOSTICS%%", string.Concat(manifest.Executions.Select(RenderDiagnostics)), StringComparison.Ordinal)
            .Replace("%%EXECUTION_COUNT%%", manifest.Executions.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string RenderHierarchy(IReadOnlyList<TestRecord> tests)
    {
        var builder = new StringBuilder();
        foreach (var domain in tests.GroupBy(test => test.Domain))
        {
            builder.Append("<section class=\"domain-group\" data-domain=\"").Append(Encode(domain.Key)).Append("\">")
                .Append("<div class=\"domain-header\"><h2>").Append(Encode(domain.Key)).Append("</h2><span>")
                .Append(domain.Count(test => test.Outcome == "Passed")).Append(" / ").Append(domain.Count()).Append(" passed</span></div>");
            foreach (var feature in domain.GroupBy(test => test.Feature))
            {
                var first = feature.First();
                builder.Append("<section class=\"feature-group\" data-feature-group=\"").Append(Encode(HierarchyKey(first))).Append("\">")
                    .Append("<div class=\"feature-header\"><div><h3>").Append(Encode(feature.Key)).Append("</h3><p>")
                    .Append(Encode(first.Why)).Append("</p></div><span>")
                    .Append(feature.Count(test => test.Outcome == "Passed")).Append(" / ").Append(feature.Count()).Append(" passed</span></div>")
                    .Append("<div class=\"feature-tests\">");
                foreach (var test in feature) builder.Append(RenderTest(test));
                builder.Append("</div></section>");
            }
            builder.Append("</section>");
        }
        return builder.ToString();
    }

    private static string RenderTest(TestRecord test)
    {
        var state = OutcomeClass(test.Outcome);
        var hasDetails = !string.IsNullOrEmpty(test.Stdout) || !string.IsNullOrEmpty(test.Stderr) ||
                         !string.IsNullOrEmpty(test.ErrorMessage) || !string.IsNullOrEmpty(test.StackTrace);
        var search = string.Join(' ', new[]
        {
            test.Name, test.Title, test.Description, test.Domain, test.Feature, test.Why,
            test.ClassName, string.Join(' ', test.Categories), test.ExecutionLabel, test.Outcome
        }).ToLowerInvariant();
        var columns = $"<span class=\"test-status\"><span class=\"status-dot {state}\"></span>{Encode(test.Outcome)}</span>" +
                      $"<span class=\"test-summary\" title=\"{Encode(test.Name)}\"><span class=\"test-title\">{Encode(test.Title)}</span><span class=\"test-description\">{Encode(test.Description)}</span></span>" +
                      $"<span class=\"duration\">{Encode(FormatDuration(test.DurationMs))}</span>" +
                      $"<span class=\"detail-label\">{(hasDetails ? "Details" : string.Empty)}</span>";
        var attributes = $"class=\"test-row {state}{(hasDetails ? " has-details" : string.Empty)}\" data-status=\"{state}\" data-feature=\"{Encode(HierarchyKey(test))}\" data-search=\"{Encode(search)}\"";
        if (!hasDetails) return $"<div {attributes}>{columns}</div>";

        var details = RenderOutput("Test output", test.Stdout) + RenderOutput("Standard error", test.Stderr) +
                      RenderOutput("Failure", test.ErrorMessage) + RenderOutput("Stack trace", test.StackTrace);
        return $"<details {attributes}{(state == "failed" ? " open" : string.Empty)}><summary>{columns}</summary>" +
               $"<div class=\"test-body\"><div class=\"test-context\"><span><strong>Test:</strong> {Encode(test.Name)}</span>" +
               $"<span><strong>Hierarchy:</strong> {Encode(test.Domain)} / {Encode(test.Feature)}</span>" +
               $"<span><strong>Source:</strong> {Encode(ShortClass(test.ClassName))}</span>" +
               $"<span><strong>Execution:</strong> {Encode(test.ExecutionLabel)}</span><span><strong>Started:</strong> {Encode(test.StartTime)}</span></div>{details}</div></details>";
    }

    private static string RenderDiagnostics(ExecutionRecord execution)
    {
        var failed = execution.ExitCode != 0 || execution.Summary.Failed > 0 || !string.IsNullOrEmpty(execution.ParseError);
        var state = failed ? "failed" : "passed";
        var links = new List<string>();
        if (!string.IsNullOrEmpty(execution.TrxPath)) links.Add($"<a href=\"{Encode(execution.TrxPath)}\">TRX</a>");
        if (!string.IsNullOrEmpty(execution.ConsoleLog)) links.Add($"<a href=\"{Encode(execution.ConsoleLog)}\">Full console log</a>");
        return $"<details class=\"diagnostic-item {state}\"{(failed ? " open" : string.Empty)}><summary>" +
               $"<span class=\"status-dot {state}\"></span><span class=\"execution-name\">{Encode(execution.Label)}</span>" +
               $"<span class=\"counts\">{execution.Summary.Passed} passed - {execution.Summary.Failed} failed - {execution.Summary.Skipped + execution.Summary.Inconclusive} other</span>" +
               $"<span class=\"exit-code\">Exit {execution.ExitCode}</span></summary><div class=\"diagnostic-body\"><dl>" +
               $"<div><dt>Started</dt><dd>{Encode(execution.StartedUtc)}</dd></div><div><dt>Completed</dt><dd>{Encode(execution.CompletedUtc)}</dd></div>" +
               $"<div><dt>Exit code</dt><dd>{execution.ExitCode}</dd></div><div><dt>Artifacts</dt><dd>{(links.Count > 0 ? string.Join(" &middot; ", links) : "None")}</dd></div></dl>" +
               RenderOutput("TRX parse error", execution.ParseError) +
               $"<details class=\"raw-diagnostics\"><summary>Command and console</summary><div class=\"raw-body\"><h5>Command</h5><pre>{Encode(execution.Command)}</pre>" +
               RenderOutput("Console output (last 200 lines)", execution.ConsoleTail) + "</div></details></div></details>";
    }

    private static string RenderOutput(string title, string value)
        => string.IsNullOrEmpty(value) ? string.Empty : $"<h5>{Encode(title)}</h5><pre>{Encode(value)}</pre>";

    private static string OutcomeClass(string outcome)
        => outcome switch
        {
            "Passed" => "passed",
            "Failed" or "Error" or "Timeout" or "Aborted" => "failed",
            "NotExecuted" or "NotRunnable" or "Disconnected" => "skipped",
            _ => "inconclusive"
        };

    private static int OutcomeRank(string outcome)
        => outcome switch
        {
            "Failed" => 0, "Error" => 1, "Timeout" => 2, "Aborted" => 3,
            "Inconclusive" => 4, "NotExecuted" => 5, "Passed" => 6, _ => 99
        };

    private static string HierarchyKey(TestRecord test) => $"{test.Domain}::{test.Feature}";
    private static string ShortClass(string value) => value.Split('.').LastOrDefault() ?? value;
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FormatDuration(double milliseconds)
    {
        if (milliseconds < 1000) return $"{milliseconds:F0} ms";
        var seconds = milliseconds / 1000;
        if (seconds < 60) return $"{seconds:F2} s";
        var minutes = (int)(seconds / 60);
        if (minutes < 60) return $"{minutes}m {seconds % 60:F1}s";
        return $"{minutes / 60}h {minutes % 60}m";
    }

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>%%PAGE_TITLE%%</title>
  <style>
    :root { color-scheme: light; --page:#f3f5f7; --surface:#fff; --line:#d7dde3; --text:#17202a; --muted:#5f6b76; --pass:#147d4a; --fail:#b42318; --warn:#8a5a00; --focus:#1769aa; --code:#111820; --code-text:#e8edf2; }
    * { box-sizing:border-box; } body { margin:0; background:var(--page); color:var(--text); font-family:"Segoe UI",Tahoma,sans-serif; font-size:14px; line-height:1.45; }
    main { width:min(1500px,calc(100% - 32px)); margin:24px auto 48px; } header { display:flex; justify-content:space-between; gap:24px; margin-bottom:18px; }
    h1 { margin:0 0 4px; font-size:24px; } h2 { margin:0; font-size:17px; } h3 { margin:0 0 2px; font-size:14px; } h5 { margin:14px 0 5px; font-size:12px; } p { margin:4px 0; } .muted { color:var(--muted); }
    .status { border:1px solid; border-radius:6px; padding:7px 12px; font-weight:700; } .status.passed { color:var(--pass); background:#e7f5ed; } .status.failed { color:var(--fail); background:#fdecea; }
    .summary-grid { display:grid; grid-template-columns:repeat(4,minmax(120px,1fr)); gap:10px; margin-bottom:14px; } .metric { background:var(--surface); border:1px solid var(--line); border-radius:6px; padding:12px; } .metric strong { display:block; font-size:22px; } .metric span { color:var(--muted); font-size:12px; }
    .controls { display:flex; align-items:center; gap:10px; flex-wrap:wrap; background:var(--surface); border:1px solid var(--line); border-radius:6px; padding:10px; margin-bottom:10px; }
    .controls input,.controls select { height:34px; border:1px solid #b9c2cb; border-radius:4px; background:#fff; padding:0 10px; font:inherit; } .controls input { flex:1 1 320px; min-width:180px; } .controls select { flex:0 1 300px; min-width:170px; }
    .filter-group { display:flex; gap:4px; } .filter-button { height:34px; border:1px solid #b9c2cb; border-radius:4px; background:#fff; padding:0 10px; cursor:pointer; } .filter-button.active { color:#fff; background:#34495e; } .visible-count { margin-left:auto; color:var(--muted); font-size:12px; }
    .test-list { background:var(--surface); border:1px solid var(--line); border-radius:6px; overflow:hidden; } .test-list-header,div.test-row,.test-row>summary { display:grid; grid-template-columns:100px minmax(0,1fr) 90px 58px; align-items:center; column-gap:12px; }
    .test-list-header { min-height:34px; padding:0 12px; background:#eef2f5; color:var(--muted); font-size:11px; font-weight:700; text-transform:uppercase; }
    .domain-group+.domain-group { border-top:2px solid #cbd4dc; } .domain-header { display:flex; justify-content:space-between; gap:16px; padding:12px 14px; background:#dfe6ec; } .domain-header span,.feature-header>span { color:var(--muted); font-size:12px; white-space:nowrap; }
    .feature-group+.feature-group { border-top:1px solid #cfd7de; } .feature-header { display:flex; justify-content:space-between; gap:18px; padding:10px 14px; background:#f4f7f9; border-top:1px solid #cfd7de; } .feature-header p { margin:0; color:var(--muted); font-size:12px; }
    .test-row { min-height:52px; border-top:1px solid #e4e8ec; } div.test-row,.test-row>summary { padding:7px 12px; } .test-row>summary { min-height:52px; cursor:pointer; list-style:none; } .test-row>summary::-webkit-details-marker { display:none; } .test-row.failed { border-left:4px solid var(--fail); } .test-row[hidden],.feature-group[hidden],.domain-group[hidden] { display:none; }
    .test-status { display:flex; align-items:center; gap:7px; font-size:12px; font-weight:700; } .status-dot { width:9px; height:9px; border-radius:50%; flex:0 0 9px; background:#53606d; } .status-dot.passed { background:var(--pass); } .status-dot.failed { background:var(--fail); } .status-dot.inconclusive { background:var(--warn); }
    .test-summary { display:flex; flex-direction:column; min-width:0; gap:2px; } .test-title { font-weight:650; overflow-wrap:anywhere; } .test-description,.duration,.detail-label { color:var(--muted); font-size:12px; overflow-wrap:anywhere; } .duration,.detail-label { text-align:right; } .duration { white-space:nowrap; } .detail-label { color:var(--focus); }
    .test-body { grid-column:1/-1; min-width:0; overflow:hidden; border-top:1px solid var(--line); background:#fafbfc; padding:10px 14px 14px; } .test-context { display:flex; flex-wrap:wrap; gap:18px; color:var(--muted); font-size:12px; margin-bottom:8px; } .test-context span { overflow-wrap:anywhere; }
    .diagnostics { margin-top:22px; background:var(--surface); border:1px solid var(--line); border-radius:6px; overflow:hidden; } .diagnostics>summary { cursor:pointer; padding:11px 13px; font-weight:700; } .diagnostics-list { border-top:1px solid var(--line); padding:8px; }
    .diagnostic-item { border:1px solid var(--line); border-radius:5px; margin:6px 0; overflow:hidden; } .diagnostic-item>summary { cursor:pointer; display:flex; align-items:center; gap:9px; padding:9px 11px; } .execution-name { font-weight:700; } .counts { flex:1; color:var(--muted); font-size:12px; } .exit-code { color:var(--muted); font-size:12px; } .diagnostic-body { border-top:1px solid var(--line); padding:11px 13px 14px; }
    dl { display:grid; grid-template-columns:repeat(4,minmax(140px,1fr)); gap:8px 18px; } dt { color:var(--muted); font-size:11px; text-transform:uppercase; } dd { margin:2px 0 0; overflow-wrap:anywhere; } pre { margin:0; background:var(--code); color:var(--code-text); border-radius:5px; padding:10px 12px; overflow:auto; white-space:pre-wrap; font-family:Consolas,monospace; font-size:12px; }
    footer { margin-top:18px; color:var(--muted); font-size:12px; } @media(max-width:720px) { header { flex-direction:column; } .summary-grid { grid-template-columns:repeat(2,1fr); } .test-list-header,div.test-row,.test-row>summary { grid-template-columns:78px minmax(0,1fr) 60px; gap:8px; } .detail-label,.test-list-header>span:last-child { display:none; } dl { grid-template-columns:1fr; } }
  </style>
</head>
<body><main>
  <header><div><h1>Regression Results</h1><p><strong>Master execution:</strong> %%MASTER_RUN_ID%%</p><p class="muted">%%EXECUTION_SUMMARY%%</p></div><div class="status %%OVERALL_CLASS%%">%%OVERALL_STATUS%%</div></header>
  <section class="summary-grid"><div class="metric"><strong>%%TOTAL%%</strong><span>Tests</span></div><div class="metric"><strong>%%PASSED%%</strong><span>Passed</span></div><div class="metric"><strong>%%FAILED%%</strong><span>Failed</span></div><div class="metric"><strong>%%OTHER%%</strong><span>Skipped / Other</span></div></section>
  <section class="controls"><input id="test-search" type="search" placeholder="Filter by feature, value, scenario, or test name"><div class="filter-group"><button class="filter-button active" data-filter="all">All %%TOTAL%%</button><button class="filter-button" data-filter="failed">Failed %%FAILED%%</button><button class="filter-button" data-filter="passed">Passed %%PASSED%%</button><button class="filter-button" data-filter="other">Other %%OTHER%%</button></div><select id="feature-filter"><option value="">All features</option>%%FEATURE_OPTIONS%%</select><span id="visible-count" class="visible-count">Showing %%TOTAL%% tests</span></section>
  <section class="test-list"><div class="test-list-header"><span>Status</span><span>Scenario and why it matters</span><span>Duration</span><span></span></div>%%HIERARCHY%%<p id="no-results" class="muted" hidden>No tests match the current filters.</p></section>
  <details class="diagnostics"><summary>Run diagnostics (%%EXECUTION_COUNT%% executions)</summary><div class="diagnostics-list">%%DIAGNOSTICS%%</div></details>
  <footer>Generated from MSTest TRX results. Refresh after another execution appends to this master run.</footer>
</main>
<script>
(() => { const rows=[...document.querySelectorAll('.test-row')], search=document.getElementById('test-search'), feature=document.getElementById('feature-filter'), count=document.getElementById('visible-count'), empty=document.getElementById('no-results'), buttons=[...document.querySelectorAll('.filter-button')], groups=[...document.querySelectorAll('.feature-group')], domains=[...document.querySelectorAll('.domain-group')]; let status='all'; function apply(){ const query=search.value.trim().toLowerCase(), selected=feature.value; let visible=0; for(const row of rows){ const state=row.dataset.status; const statusMatch=status==='all'||state===status||(status==='other'&&state!=='passed'&&state!=='failed'); row.hidden=!(statusMatch&&(!query||row.dataset.search.includes(query))&&(!selected||row.dataset.feature===selected)); if(!row.hidden)visible++; } for(const group of groups)group.hidden=![...group.querySelectorAll('.test-row')].some(row=>!row.hidden); for(const domain of domains)domain.hidden=![...domain.querySelectorAll('.feature-group')].some(group=>!group.hidden); count.textContent=`Showing ${visible} of ${rows.length} tests`; empty.hidden=visible!==0; } for(const button of buttons)button.addEventListener('click',()=>{ status=button.dataset.filter; for(const item of buttons)item.classList.toggle('active',item===button); apply(); }); search.addEventListener('input',apply); feature.addEventListener('change',apply); })();
</script></body></html>
""";
}

internal static class LandingReport
{
    public static string Render(IReadOnlyList<HistoryEntry> entries, IReadOnlyList<string> domains)
        {
                var latest = entries.FirstOrDefault();
                var latestMarkup = latest == null
                        ? "<p class=\"empty\">No regression runs have been recorded.</p>"
            : RenderLatest(latest, domains.Count);
                var historyRows = entries.Count == 0
                        ? string.Empty
            : string.Concat(entries.Select(entry => RenderHistoryRow(entry, domains)));
                var purposeOptions = string.Concat(entries
                    .SelectMany(entry => entry.Manifest.Executions)
                    .Select(execution => execution.Label)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .Select(value => $"<option value=\"{Encode(value)}\">{Encode(value)}</option>"));

                return Template
                        .Replace("%%LATEST%%", latestMarkup, StringComparison.Ordinal)
                        .Replace("%%HISTORY%%", historyRows, StringComparison.Ordinal)
                    .Replace("%%PURPOSE_OPTIONS%%", purposeOptions, StringComparison.Ordinal)
                        .Replace("%%RUN_COUNT%%", entries.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace("%%UPDATED%%", WebUtility.HtmlEncode(DateTimeOffset.UtcNow.ToString("u", CultureInfo.InvariantCulture)), StringComparison.Ordinal);
        }

        private static string RenderLatest(HistoryEntry entry, int domainCount)
        {
                var summary = Summarize(entry.Manifest);
                var state = summary.Failed > 0 ? "failed" : "passed";
                var labels = entry.Manifest.Executions.Select(execution => execution.Label)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
                var contents = labels.Count == 0 ? "Unlabeled regression run" : string.Join(", ", labels);
                var parsed = DateTimeOffset.TryParseExact(entry.FolderName, "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp);
                var dateHeading = parsed
                    ? parsedTimestamp.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
                    : entry.FolderName;
                var timeLabel = parsed
                    ? parsedTimestamp.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture)
                    : entry.FolderName;
                var dateTime = parsed
                    ? parsedTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : string.Empty;
                var exercisedDomains = entry.Manifest.Executions.SelectMany(execution => execution.Tests)
                    .Select(test => test.Domain).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal).Count();
                var statusText = state == "failed" ? "Failed" : "Passed";
                var resultValue = state == "failed"
                    ? summary.Failed.ToString(CultureInfo.InvariantCulture)
                    : $"{summary.Passed}<span>/{summary.Total}</span>";
                var resultCaption = state == "failed"
                    ? summary.Failed == 1 ? "issue to review" : "issues to review"
                    : "tests passed";
                return $"<a class=\"latest {state}\" href=\"{Encode(entry.ReportPath)}\" aria-label=\"Open latest report from {Encode(dateHeading)}: {Encode(contents)}\">" +
                    $"<div class=\"latest-copy\"><div class=\"latest-kicker\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span><span>Latest execution</span></div>" +
                    $"<h2><time datetime=\"{dateTime}\">{Encode(dateHeading)}</time></h2><p class=\"latest-time\">{Encode(timeLabel)} &middot; {Encode(contents)}</p>" +
                    $"<p class=\"coverage\">{exercisedDomains} of {domainCount} domains exercised &middot; {Encode(FeatureList(summary.Features))}</p></div>" +
                    $"<div class=\"latest-result\"><strong>{resultValue}</strong><small>{resultCaption}</small><span class=\"open\">View report <span aria-hidden=\"true\">&#8594;</span></span></div></a>";
        }

        private static string RenderHistoryRow(HistoryEntry entry, IReadOnlyList<string> domains)
        {
                var summary = Summarize(entry.Manifest);
                var state = summary.Failed > 0 ? "failed" : "passed";
            var labels = entry.Manifest.Executions.Select(execution => execution.Label)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList();
            var contents = labels.Count == 0 ? "Unlabeled regression run" : string.Join(", ", labels);
            var parsed = DateTimeOffset.TryParseExact(entry.FolderName, "yyyyMMdd-HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp);
            var dateHeading = parsed
                ? parsedTimestamp.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
                : entry.FolderName;
            var timeLabel = parsed
                ? parsedTimestamp.ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : entry.FolderName;
            var dateTime = parsed
                ? parsedTimestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                : string.Empty;
            var date = !parsed
                ? string.Empty
                : parsedTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var statusText = state == "failed" ? "Failed" : "Passed";
            var resultValue = state == "failed"
                ? summary.Failed.ToString(CultureInfo.InvariantCulture)
                : $"{summary.Passed}/{summary.Total}";
            var resultCaption = state == "failed"
                ? summary.Failed == 1 ? "issue to review" : "issues to review"
                : "tests passed";
            var allTests = entry.Manifest.Executions.SelectMany(execution => execution.Tests).ToList();
            var executedDomains = allTests.Select(test => test.Domain).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).ToList();
            var searchText = string.Join(" ", labels.Concat(summary.Features).Concat(executedDomains).Append(entry.FolderName));
            var purposes = JsonSerializer.Serialize(labels);
            var children = string.Concat(entry.Manifest.Executions.Select(RenderChildRun));
            var domainMarkup = string.Concat(domains.Select(domain =>
            {
                var tests = allTests.Where(test => string.Equals(test.Domain, domain, StringComparison.Ordinal))
                    .OrderBy(test => test.Feature, StringComparer.Ordinal)
                    .ThenBy(test => test.Title, StringComparer.Ordinal)
                    .ToList();
                var passed = tests.Count(test => test.Outcome == "Passed");
                var failed = tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
                var other = tests.Count - passed - failed;
                var domainState = tests.Count == 0 ? "not-run" : failed > 0 ? "failed" : other > 0 ? "other" : "passed";
                var domainStatus = domainState switch
                {
                    "passed" => "Passed",
                    "failed" => "Failed",
                    "other" => "Other",
                    _ => "Not run"
                };
                var domainCounts = domainState switch
                {
                    "passed" => $"{passed}/{tests.Count} passed",
                    "failed" => $"{failed} failed &middot; {passed} passed{(other > 0 ? $" &middot; {other} other" : string.Empty)}",
                    "other" => $"{other} other &middot; {passed} passed",
                    _ => "No tests recorded"
                };
                if (tests.Count == 0)
                {
                    return $"<div class=\"domain-card not-run\"><span class=\"domain-name\"><span class=\"status-badge not-run\"><span class=\"status-dot\"></span>{domainStatus}</span><strong>{Encode(domain)}</strong></span><span class=\"domain-counts\">{domainCounts}</span><span></span></div>";
                }

                var testMarkup = string.Concat(tests.Select(test =>
                {
                    var testState = test.Outcome switch
                    {
                        "Passed" => "passed",
                        "Failed" or "Error" or "Timeout" or "Aborted" => "failed",
                        _ => "other"
                    };
                    var title = string.IsNullOrWhiteSpace(test.Title) ? test.Name : test.Title;
                    return $"<div class=\"domain-test\"><span class=\"status-badge {testState}\"><span class=\"status-dot\"></span>{Encode(test.Outcome)}</span>" +
                        $"<div><strong>{Encode(title)}</strong><p>{Encode(test.Feature)}</p></div></div>";
                }));
                var open = domainState is "failed" or "other" ? " open" : string.Empty;
                return $"<details class=\"domain-card {domainState}\"{open}><summary><span class=\"domain-name\"><span class=\"status-badge {domainState}\"><span class=\"status-dot\"></span>{domainStatus}</span>" +
                    $"<strong>{Encode(domain)}</strong><span class=\"expanded-label\">Expanded</span></span><span class=\"domain-counts\">{domainCounts}</span><span class=\"domain-chevron\" aria-hidden=\"true\">&#8250;</span></summary>" +
                    $"<div class=\"domain-tests\">{testMarkup}</div></details>";
            }));
            var executionIssues = entry.Manifest.Executions
                .Where(execution => (execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError)) &&
                    !execution.Tests.Any(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted"))
                .Select(execution =>
                {
                    var reason = !string.IsNullOrEmpty(execution.ParseError)
                        ? execution.ParseError
                        : $"exit code {execution.ExitCode}";
                    return $"<li><strong>{Encode(execution.Label)}</strong>: {Encode(reason)}; no failed domain test result was recorded.</li>";
                })
                .ToList();
            var executionIssueMarkup = executionIssues.Count == 0
                ? string.Empty
                : $"<div class=\"execution-issues\"><strong>Execution issues</strong><ul>{string.Concat(executionIssues)}</ul></div>";
            return $"<details class=\"history-entry\" data-state=\"{state}\" data-search=\"{Encode(searchText)}\" data-purposes=\"{Encode(purposes)}\" data-date=\"{date}\">" +
                $"<summary class=\"history-row\"><span class=\"timeline-marker {state}\"><span></span></span>" +
                $"<div class=\"run-identity\"><div class=\"run-meta\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span><span>{Encode(timeLabel)}</span></div>" +
                $"<h3><time datetime=\"{dateTime}\">{Encode(dateHeading)}</time></h3><p class=\"run-purpose\">{Encode(contents)}</p></div>" +
                $"<div class=\"run-results\"><strong>{resultValue}</strong><span>{resultCaption}</span></div>" +
                $"<span class=\"disclosure\" aria-hidden=\"true\">&#8250;</span></summary>" +
                $"<div class=\"history-details\"><div class=\"detail-heading\"><div><span class=\"detail-label\">Run scope</span><p>{Encode(contents)}</p></div>" +
                $"<a class=\"report-action\" href=\"{Encode(entry.ReportPath)}\">Open report <span aria-hidden=\"true\">&#8594;</span></a></div>" +
                $"{executionIssueMarkup}<div class=\"domain-heading\"><h4>Domain status</h4><span>{domains.Count} domains</span></div><div class=\"domain-list\">{domainMarkup}</div>" +
                $"<details class=\"execution-breakdown\"><summary>Execution details ({RunCount(entry.Manifest.Executions.Count, "run")})</summary><div class=\"child-list\">{children}</div></details></div></details>";
        }

        private static string RenderChildRun(ExecutionRecord execution)
        {
            var features = execution.Tests.Select(test => test.Feature).Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
            var failed = execution.Tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
            var state = failed > 0 || execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError) ? "failed" : "passed";
            var statusText = state == "failed" ? "Failed" : "Passed";
            var issueCount = Math.Max(failed, 1);
            var resultValue = state == "failed"
                ? issueCount.ToString(CultureInfo.InvariantCulture)
                : $"{execution.Summary.Passed}/{execution.Tests.Count}";
            var resultCaption = state == "failed"
                ? issueCount == 1 ? "issue to review" : "issues to review"
                : "tests passed";
            return $"<div class=\"child-row\"><span class=\"status-badge {state}\"><span class=\"status-dot\"></span>{statusText}</span>" +
                $"<div><strong>{Encode(execution.Label)}</strong><p>{Encode(FeatureList(features))}</p></div>" +
                $"<span class=\"child-result\"><strong>{resultValue}</strong> {resultCaption}</span></div>";
        }

        private static LandingSummary Summarize(ReportManifest manifest)
        {
                var tests = manifest.Executions.SelectMany(execution => execution.Tests).ToList();
            var failed = manifest.Executions.Sum(execution =>
            {
                var failedTests = execution.Tests.Count(test => test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted");
                return failedTests > 0
                ? failedTests
                : execution.ExitCode != 0 || !string.IsNullOrEmpty(execution.ParseError) ? 1 : 0;
            });
                var passed = tests.Count(test => test.Outcome == "Passed");
                var features = tests.Select(test => test.Feature).Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToList();
                return new LandingSummary(tests.Count, passed, failed, tests.Count - passed - tests.Count(test =>
                    test.Outcome is "Failed" or "Error" or "Timeout" or "Aborted"), features);
        }

            private static string FeatureList(IReadOnlyCollection<string> features)
                => features.Count == 0 ? "Legacy run without feature metadata" : string.Join(", ", features);

            private static string RunCount(int count, string noun)
                => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

        private static string Encode(string value) => WebUtility.HtmlEncode(value);

            private sealed record LandingSummary(int Total, int Passed, int Failed, int Other, IReadOnlyList<string> Features);

        private const string Template = """
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Regression test runs</title>
    <style>
        :root { color-scheme:light; --page:#f5f7f8; --surface:#ffffff; --surface-soft:#eef2f4; --line:#d8dee3; --line-strong:#bdc7cf; --text:#151b1f; --muted:#5d6871; --quiet:#7c8790; --accent:#0869b8; --accent-hover:#075898; --pass:#16784a; --pass-soft:#e6f4ec; --fail:#b42318; --fail-soft:#fcebea; --other:#8a5a00; --other-soft:#fff4d6; --not-run:#68747d; --not-run-soft:#edf1f3; --focus:#1473e6; }
        * { box-sizing:border-box; } html { background:var(--page); } body { margin:0; min-width:320px; background:var(--page); color:var(--text); font-family:"Aptos","Segoe UI Variable",sans-serif; font-size:15px; line-height:1.45; }
        button,input,select { font:inherit; letter-spacing:0; } button,summary,a,input,select { -webkit-tap-highlight-color:transparent; } [hidden] { display:none !important; }
        main { width:min(1160px,calc(100% - 40px)); margin:0 auto 64px; } .masthead { display:flex; align-items:flex-start; justify-content:space-between; gap:24px; padding:42px 0 24px; }
        .eyebrow { display:block; margin-bottom:6px; color:var(--accent); font-size:12px; font-weight:700; text-transform:uppercase; } h1,h2,h3,h4,p { margin:0; } h1 { font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:38px; font-weight:650; line-height:1.08; letter-spacing:0; } .subtitle { max-width:720px; margin-top:9px; color:var(--muted); font-size:16px; }
        .help { position:relative; z-index:3; } .help>summary { display:grid; width:34px; height:34px; place-items:center; border:1px solid var(--line-strong); border-radius:50%; background:var(--surface); color:var(--muted); cursor:pointer; font-weight:750; list-style:none; } .help>summary::-webkit-details-marker { display:none; } .help>summary:hover { color:var(--text); border-color:var(--quiet); } .help-panel { position:absolute; top:44px; right:0; width:320px; padding:16px; background:var(--surface); border:1px solid var(--line); border-radius:8px; box-shadow:0 16px 44px rgba(27,39,47,.14); } .help-panel h2 { font-size:16px; } .help-panel dl { margin:12px 0 0; } .help-panel dt { margin-top:10px; font-weight:700; } .help-panel dd { margin:2px 0 0; color:var(--muted); font-size:13px; }
        .latest { display:grid; grid-template-columns:minmax(0,1fr) 180px; align-items:stretch; min-height:210px; color:inherit; text-decoration:none; background:var(--surface); border:1px solid var(--line); border-radius:8px; overflow:hidden; box-shadow:0 1px 1px rgba(20,31,38,.03); } .latest:hover { border-color:var(--line-strong); box-shadow:0 12px 32px rgba(24,39,49,.08); } .latest-copy { min-width:0; padding:30px 32px; } .latest-kicker { display:flex; align-items:center; gap:12px; color:var(--muted); font-size:13px; font-weight:650; } .latest h2 { max-width:780px; margin-top:18px; font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:30px; font-weight:650; line-height:1.18; overflow-wrap:anywhere; } .latest-time { margin-top:10px; color:var(--muted); } .coverage { margin-top:5px; color:var(--quiet); font-size:13px; }
        .latest-result { display:flex; min-width:180px; padding:28px; align-items:flex-start; flex-direction:column; justify-content:center; background:var(--surface-soft); border-left:1px solid var(--line); } .latest.passed .latest-result { background:var(--pass-soft); } .latest.failed .latest-result { background:var(--fail-soft); } .latest-result>strong { font-size:34px; font-weight:700; line-height:1; } .latest-result>strong span { color:var(--muted); font-size:20px; } .latest-result small { margin-top:6px; color:var(--muted); } .open { margin-top:auto; color:var(--accent); font-weight:700; white-space:nowrap; }
        .status-badge { display:inline-flex; width:max-content; min-height:24px; padding:2px 8px; align-items:center; gap:6px; border-radius:999px; font-size:12px; font-weight:750; line-height:1; } .status-badge.passed { background:var(--pass-soft); color:var(--pass); } .status-badge.failed { background:var(--fail-soft); color:var(--fail); } .status-badge.other { background:var(--other-soft); color:var(--other); } .status-badge.not-run { background:var(--not-run-soft); color:var(--not-run); } .status-dot { width:7px; height:7px; flex:0 0 7px; border-radius:50%; background:currentColor; }
        .history { margin-top:44px; } .history-heading { display:flex; align-items:flex-end; justify-content:space-between; gap:20px; padding-bottom:14px; border-bottom:1px solid var(--line-strong); } .history-heading h2 { font-family:"Aptos Display","Segoe UI Variable Display",sans-serif; font-size:25px; font-weight:650; } .history-heading p { margin-top:3px; color:var(--muted); font-size:13px; } #visible-count { color:var(--muted); font-size:13px; white-space:nowrap; }
        .filters { display:grid; grid-template-columns:minmax(220px,1.5fr) minmax(180px,1fr) auto auto auto; gap:12px; padding:18px 0; align-items:end; border-bottom:1px solid var(--line); } .field { display:flex; min-width:0; flex-direction:column; gap:5px; color:var(--muted); font-size:12px; font-weight:650; } .field input,.field select { width:100%; height:42px; padding:0 12px; color:var(--text); background:var(--surface); border:1px solid var(--line-strong); border-radius:7px; outline:none; } .field input:focus,.field select:focus { border-color:var(--focus); box-shadow:0 0 0 3px rgba(20,115,230,.13); } .field input::placeholder { color:var(--quiet); } .date-field { width:150px; }
        .failure-toggle { display:flex; height:42px; padding:0 12px; align-items:center; gap:9px; color:var(--text); background:var(--surface); border:1px solid var(--line-strong); border-radius:7px; cursor:pointer; white-space:nowrap; } .failure-toggle input { position:absolute; width:1px; height:1px; opacity:0; } .toggle-track { position:relative; width:30px; height:18px; flex:0 0 30px; border-radius:999px; background:#a5afb7; } .toggle-track::after { position:absolute; top:3px; left:3px; width:12px; height:12px; border-radius:50%; background:#fff; content:""; transition:transform .16s ease; } .failure-toggle input:checked+.toggle-track { background:var(--fail); } .failure-toggle input:checked+.toggle-track::after { transform:translateX(12px); } .failure-toggle:has(input:focus-visible) { outline:3px solid rgba(20,115,230,.2); outline-offset:2px; }
        .clear-button { height:42px; padding:0 4px; color:var(--accent); background:transparent; border:0; cursor:pointer; font-weight:700; } .clear-button:hover { color:var(--accent-hover); text-decoration:underline; }
        .history-list { position:relative; } .history-list::before { position:absolute; top:0; bottom:0; left:19px; width:1px; background:var(--line); content:""; } .history-entry { position:relative; border-bottom:1px solid var(--line); } .history-entry>summary { list-style:none; cursor:pointer; } .history-entry>summary::-webkit-details-marker { display:none; } .history-entry[open] { background:var(--surface); } .history-entry[open] .disclosure { transform:rotate(90deg); }
        .history-row { display:grid; grid-template-columns:40px minmax(0,1fr) 110px 24px; min-height:96px; padding:19px 8px 19px 0; align-items:center; gap:16px; } .history-row:hover { background:rgba(255,255,255,.55); } .timeline-marker { position:relative; z-index:1; display:grid; width:40px; height:40px; place-items:center; } .timeline-marker>span { width:11px; height:11px; border:3px solid var(--page); border-radius:50%; background:var(--pass); box-shadow:0 0 0 1px var(--pass); } .timeline-marker.failed>span { background:var(--fail); box-shadow:0 0 0 1px var(--fail); }
        .run-identity { min-width:0; } .run-meta { display:flex; align-items:center; gap:10px; } .run-meta>span:last-child { color:var(--muted); font-size:12px; } .run-identity h3 { margin-top:8px; font-size:18px; font-weight:680; line-height:1.25; overflow-wrap:anywhere; } .run-purpose { margin-top:3px; color:var(--muted); font-size:13px; overflow-wrap:anywhere; } .run-results { display:flex; flex-direction:column; align-items:flex-end; } .run-results strong { font-size:17px; } .run-results span { color:var(--muted); font-size:12px; } .disclosure { color:var(--muted); font-family:Georgia,serif; font-size:27px; line-height:1; transition:transform .16s ease; }
        .history-details { margin-left:56px; padding:0 48px 22px 20px; border-left:2px solid #b8d9c8; } .history-entry[data-state="failed"]>.history-details { border-left-color:#e4b3af; } .detail-heading { display:flex; justify-content:space-between; align-items:flex-start; gap:24px; padding:18px 0; border-top:1px solid var(--line); } .detail-label { color:var(--muted); font-size:11px; font-weight:750; text-transform:uppercase; } .detail-heading p { margin-top:3px; color:var(--text); } .report-action { display:inline-flex; min-height:38px; padding:0 13px; align-items:center; gap:8px; color:#fff; background:var(--accent); border-radius:7px; text-decoration:none; font-weight:700; white-space:nowrap; } .report-action:hover { background:var(--accent-hover); }
        .execution-issues { margin:0 0 18px; padding:13px 15px; color:#6f4700; background:var(--other-soft); border:1px solid #e8cf8b; border-radius:7px; } .execution-issues ul { margin:6px 0 0; padding-left:20px; } .execution-issues li+li { margin-top:4px; }
        .domain-heading { display:flex; align-items:center; justify-content:space-between; gap:16px; margin:4px 0 10px; } .domain-heading h4 { font-size:16px; } .domain-heading>span { color:var(--muted); font-size:12px; } .domain-list { display:grid; gap:9px; }
        .domain-card { background:var(--surface); border:1px solid var(--line); border-left:4px solid var(--pass); border-radius:7px; overflow:hidden; } .domain-card.failed { border-left-color:var(--fail); } .domain-card.other { border-left-color:var(--other); } .domain-card.not-run { border-left-color:#9aa5ad; background:#fafbfc; } .domain-card[open] { position:relative; overflow:visible; background:transparent; border:0; border-radius:0; box-shadow:none; } .domain-card[open]::after { position:absolute; top:58px; left:13px; width:15px; height:17px; border-bottom:2px solid #9bcbb1; border-left:2px solid #9bcbb1; border-bottom-left-radius:7px; content:""; } .domain-card.failed[open]::after { border-color:#e5aaa5; } .domain-card.other[open]::after { border-color:#dfc675; }
        .domain-card>summary,.domain-card.not-run { display:grid; grid-template-columns:minmax(0,1fr) auto 28px; min-height:58px; padding:10px 12px; align-items:center; gap:12px; list-style:none; } .domain-card>summary { cursor:pointer; } .domain-card>summary::-webkit-details-marker { display:none; } .domain-card>summary:hover { background:#fafbfc; } .domain-card[open]>summary { background:#edf8f2; border:1px solid #9bcbb1; border-left:4px solid var(--pass); border-radius:7px; box-shadow:0 0 0 1px rgba(22,120,74,.1),0 5px 14px rgba(26,48,37,.06); } .domain-card.failed[open]>summary { background:var(--fail-soft); border-color:#e5aaa5; border-left-color:var(--fail); } .domain-card.other[open]>summary { background:var(--other-soft); border-color:#dfc675; border-left-color:var(--other); } .domain-card[open] .domain-chevron { color:var(--pass); background:#d9eee2; transform:rotate(90deg); } .domain-card.failed[open] .domain-chevron { color:var(--fail); background:#f7d9d6; } .domain-card.other[open] .domain-chevron { color:var(--other); background:#f3e5b4; }
        .domain-name { display:flex; min-width:0; align-items:center; flex-wrap:wrap; gap:8px 10px; } .domain-name>strong { overflow-wrap:anywhere; } .expanded-label { display:none; padding:3px 7px; color:var(--pass); background:#d9eee2; border-radius:999px; font-size:11px; font-weight:750; line-height:1; } .domain-card[open] .expanded-label { display:inline-flex; } .domain-card.failed[open] .expanded-label { color:var(--fail); background:#f7d9d6; } .domain-card.other[open] .expanded-label { color:var(--other); background:#f3e5b4; } .domain-counts { color:var(--muted); font-size:12px; text-align:right; white-space:nowrap; } .domain-chevron { display:grid; width:28px; height:28px; place-items:center; color:var(--muted); background:var(--not-run-soft); border-radius:50%; font-family:Georgia,serif; font-size:24px; line-height:1; transition:transform .16s ease; }
        .domain-tests { margin:8px 0 0 28px; overflow:hidden; background:#fbfcfc; border:1px solid var(--line); border-left:4px solid var(--pass); border-radius:7px; } .domain-card.failed .domain-tests { border-left-color:var(--fail); } .domain-card.other .domain-tests { border-left-color:var(--other); } .domain-test { display:grid; grid-template-columns:86px minmax(0,1fr); align-items:center; gap:12px; min-height:58px; padding:9px 14px; } .domain-test+.domain-test { border-top:1px solid #e8ecef; } .domain-test>div { min-width:0; } .domain-test strong { overflow-wrap:anywhere; } .domain-test p { margin-top:2px; color:var(--muted); font-size:12px; }
        .execution-breakdown { margin-top:18px; border-top:1px solid var(--line); } .execution-breakdown>summary { padding:13px 0; color:var(--muted); cursor:pointer; font-size:13px; font-weight:700; list-style:none; } .execution-breakdown>summary::-webkit-details-marker { display:none; } .execution-breakdown>summary::after { margin-left:7px; content:"+"; } .execution-breakdown[open]>summary::after { content:"-"; } .child-list { border-top:1px solid var(--line); } .child-row { display:grid; grid-template-columns:86px minmax(0,1fr) 110px; align-items:center; gap:14px; padding:12px 0; } .child-row+.child-row { border-top:1px solid #e4e8ec; } .child-row>div { min-width:0; } .child-row>div>strong { overflow-wrap:anywhere; } .child-row p { margin-top:2px; color:var(--muted); font-size:12px; } .child-result { color:var(--muted); font-size:12px; text-align:right; } .child-result strong { color:var(--text); }
        .no-results,.empty { padding:36px 12px; color:var(--muted); text-align:center; } footer { display:flex; justify-content:space-between; gap:20px; margin-top:22px; color:var(--quiet); font-size:12px; }
        summary:focus-visible,a:focus-visible,button:focus-visible { outline:3px solid rgba(20,115,230,.25); outline-offset:3px; }
        @media(max-width:900px) { .filters { grid-template-columns:1fr 1fr 150px 150px; } .failure-toggle { grid-column:1/2; width:max-content; } .clear-button { justify-self:start; } }
        @media(max-width:680px) { main { width:min(100% - 28px,1160px); margin-bottom:40px; } .masthead { padding-top:28px; } h1 { font-size:31px; } .subtitle { font-size:14px; } .latest { grid-template-columns:1fr; min-height:0; } .latest-copy { padding:24px 22px; } .latest h2 { font-size:24px; } .latest-result { min-width:0; padding:18px 22px; border-top:1px solid var(--line); border-left:0; flex-flow:row wrap; align-items:center; justify-content:flex-start; gap:6px 10px; } .latest-result>strong { font-size:25px; } .latest-result>strong span { font-size:16px; } .latest-result small { margin-top:0; } .latest-result .open { margin:0 0 0 auto; } .history { margin-top:34px; } .filters { grid-template-columns:1fr 1fr; } .search-field,.purpose-field { grid-column:1/-1; } .date-field { width:auto; } .history-row { grid-template-columns:36px minmax(0,1fr) 22px; min-height:128px; gap:10px; padding-right:4px; } .timeline-marker { width:36px; } .history-list::before { left:17px; } .run-results { grid-column:2; align-items:baseline; flex-flow:row; gap:6px; } .disclosure { grid-column:3; grid-row:1/3; } .history-details { margin-left:42px; padding:0 4px 20px 10px; } .detail-heading { align-items:flex-start; flex-direction:column; } .domain-card>summary,.domain-card.not-run { grid-template-columns:minmax(0,1fr) 20px; gap:8px; } .domain-counts { grid-column:1; text-align:left; white-space:normal; } .domain-card.not-run>span:last-child { display:none; } .domain-card[open]::after { left:9px; width:15px; } .domain-tests { margin-left:24px; } .domain-test { grid-template-columns:78px minmax(0,1fr); padding-inline:10px; } .child-row { grid-template-columns:80px minmax(0,1fr); } .child-result { grid-column:2; text-align:left; } footer { flex-direction:column; gap:3px; } }
        @media(max-width:440px) { .filters { grid-template-columns:1fr; } .search-field,.purpose-field { grid-column:auto; } .help-panel { width:min(320px,calc(100vw - 28px)); } .latest-result { align-items:flex-start; flex-direction:column; } .latest-result .open { margin:8px 0 0; } .date-field { width:100%; } }
        @media(prefers-reduced-motion:reduce) { .toggle-track::after,.disclosure { transition:none; } }
    </style>
</head>
<body><main>
    <header class="masthead"><div><span class="eyebrow">Quality assurance</span><h1>Regression test runs</h1><p class="subtitle">Reports by execution date, with status across all seven regression domains.</p></div>
        <details class="help"><summary aria-label="About regression results" title="About regression results">i</summary><div class="help-panel"><h2>About these results</h2><dl><dt>Report date</dt><dd>The UTC date and time when the master execution started.</dd><dt>Domain status</dt><dd>Passed, Failed, Other, or Not run. Not run means the report contains no test result for that domain.</dd><dt>Test count</dt><dd>The number executed; parameterized tests count once per data row.</dd></dl></div></details>
    </header>
    %%LATEST%%
    <section class="history" aria-labelledby="history-title"><div class="history-heading"><div><h2 id="history-title">Run history</h2><p>Newest execution first</p></div><span id="visible-count" aria-live="polite">%%RUN_COUNT%% runs</span></div>
        <div class="filters" role="search"><label class="field search-field">Search<input id="run-search" type="search" placeholder="Run purpose or feature"></label>
            <label class="field purpose-field">Purpose<select id="purpose-filter"><option value="">All purposes</option>%%PURPOSE_OPTIONS%%</select></label>
            <label class="field date-field">From<input id="date-from" type="date"></label><label class="field date-field">To<input id="date-to" type="date"></label>
            <label class="failure-toggle"><input id="failures-only" type="checkbox"><span class="toggle-track" aria-hidden="true"></span><span>Failures only</span></label><button class="clear-button" id="clear-filters" type="button" hidden>Clear filters</button>
        </div><div class="history-list">%%HISTORY%%<p class="no-results" id="no-results" hidden>No runs match these filters.</p></div>
    </section>
    <footer><span>Generated from MSTest TRX results</span><span>Updated %%UPDATED%%</span></footer>
</main>
<script>
(() => {
    const rows = [...document.querySelectorAll('.history-entry')];
    const search = document.getElementById('run-search');
    const purpose = document.getElementById('purpose-filter');
    const dateFrom = document.getElementById('date-from');
    const dateTo = document.getElementById('date-to');
    const failuresOnly = document.getElementById('failures-only');
    const clear = document.getElementById('clear-filters');
    const count = document.getElementById('visible-count');
    const empty = document.getElementById('no-results');
    function apply() {
        const query = search.value.trim().toLowerCase();
        const selectedPurpose = purpose.value;
        let visible = 0;
        for (const row of rows) {
            const purposes = JSON.parse(row.dataset.purposes || '[]');
            const matches = (!query || row.dataset.search.toLowerCase().includes(query)) &&
                (!selectedPurpose || purposes.includes(selectedPurpose)) &&
                (!dateFrom.value || row.dataset.date >= dateFrom.value) &&
                (!dateTo.value || row.dataset.date <= dateTo.value) &&
                (!failuresOnly.checked || row.dataset.state === 'failed');
            row.hidden = !matches;
            if (matches) visible++;
        }
        dateFrom.max = dateTo.value;
        dateTo.min = dateFrom.value;
        count.textContent = `${visible} of ${rows.length} runs`;
        empty.hidden = visible !== 0;
        clear.hidden = !(query || selectedPurpose || dateFrom.value || dateTo.value || failuresOnly.checked);
    }
    for (const control of [search, purpose, dateFrom, dateTo, failuresOnly]) {
        control.addEventListener(control === search ? 'input' : 'change', apply);
    }
    clear.addEventListener('click', () => {
        search.value = '';
        purpose.value = '';
        dateFrom.value = '';
        dateTo.value = '';
        failuresOnly.checked = false;
        apply();
        search.focus();
    });
    apply();
})();
</script></body></html>
""";
}

internal sealed class CommandOptions
{
    public required string ManifestPath { get; init; }
    public required string HtmlPath { get; init; }
    public required string LandingPath { get; init; }
    public required string HistoryRootPath { get; init; }
    public required string MasterRunId { get; init; }
    public required string ExecutionId { get; init; }
    public required string Label { get; init; }
    public required string TrxPath { get; init; }
    public required string ConsoleLogPath { get; init; }
    public required string TestAssemblyPath { get; init; }
    public required int ExitCode { get; init; }
    public required string Command { get; init; }
    public required string StartedUtc { get; init; }
    public required string CompletedUtc { get; init; }

    public static CommandOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Arguments must be supplied as --name value pairs.");
            values[args[index]] = args[index + 1];
        }
        string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new ArgumentException($"Missing required argument {key}.");
        string Text(string key) => values.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException($"Missing required argument {key}.");

        return new CommandOptions
        {
            ManifestPath = Required("--manifest"), HtmlPath = Required("--html"),
            LandingPath = Required("--landing"), HistoryRootPath = Required("--history-root"),
            MasterRunId = Text("--master-run-id"),
            ExecutionId = Text("--execution-id"), Label = Text("--label"), TrxPath = Required("--trx"),
            ConsoleLogPath = Required("--console-log"), TestAssemblyPath = Required("--test-assembly"),
            ExitCode = int.Parse(Text("--exit-code"), CultureInfo.InvariantCulture), Command = Text("--command"),
            StartedUtc = Text("--started-utc"), CompletedUtc = Text("--completed-utc")
        };
    }
}

internal sealed record HistoryEntry(string FolderName, string ReportPath, ReportManifest Manifest);

internal sealed class ReportManifest
{
    public int SchemaVersion { get; set; } = 3;
    public string MasterRunId { get; set; } = string.Empty;
    public string CreatedUtc { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;
    public List<ExecutionRecord> Executions { get; set; } = [];
}

internal sealed class ExecutionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string StartedUtc { get; set; } = string.Empty;
    public string CompletedUtc { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string TrxPath { get; set; } = string.Empty;
    public string ConsoleLog { get; set; } = string.Empty;
    public string ConsoleTail { get; set; } = string.Empty;
    public string ParseError { get; set; } = string.Empty;
    public string TrxRunId { get; set; } = string.Empty;
    public string TrxRunName { get; set; } = string.Empty;
    public string TrxStartedUtc { get; set; } = string.Empty;
    public string TrxCompletedUtc { get; set; } = string.Empty;
    public List<TestRecord> Tests { get; set; } = [];
    public Summary Summary { get; set; } = new();
}

internal sealed class TestRecord
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DefinitionName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public string Outcome { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Feature { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
    public string ExecutionLabel { get; set; } = string.Empty;
}

internal sealed class Summary
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Inconclusive { get; set; }
    public int Other { get; set; }
    public double DurationMs { get; set; }

    public static Summary From(IEnumerable<TestRecord> tests)
    {
        var result = new Summary();
        foreach (var test in tests)
        {
            result.Total++;
            result.DurationMs += test.DurationMs;
            switch (test.Outcome)
            {
                case "Passed": result.Passed++; break;
                case "Failed" or "Error" or "Timeout" or "Aborted": result.Failed++; break;
                case "NotExecuted" or "NotRunnable" or "Disconnected": result.Skipped++; break;
                case "Inconclusive" or "Warning": result.Inconclusive++; break;
                default: result.Other++; break;
            }
        }
        result.DurationMs = Math.Round(result.DurationMs, 3);
        return result;
    }
}

internal sealed class TrxDefinition
{
    public string Name { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
}
