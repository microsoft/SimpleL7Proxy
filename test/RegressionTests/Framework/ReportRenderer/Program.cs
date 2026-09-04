using System.Globalization;
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
            ParseTrx(options, catalog, execution);
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

    private static void ParseTrx(CommandOptions options, MetadataCatalog catalog, ExecutionRecord execution)
    {
        var document = XDocument.Load(options.TrxPath);
        var root = document.Root ?? throw new InvalidDataException("TRX has no root element.");
        var artifactsRoot = Path.Combine(Path.GetDirectoryName(options.TrxPath)!, "artifacts");
        if (Directory.Exists(artifactsRoot))
        {
            Directory.Delete(artifactsRoot, recursive: true);
        }
        Directory.CreateDirectory(artifactsRoot);
        var artifactDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            var resultFiles = result.Element(TrxNamespace + "ResultFiles")?
                .Elements(TrxNamespace + "ResultFile")
                .Select(element => Attribute(element, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(path, Path.GetDirectoryName(options.TrxPath)!))
                .Where(File.Exists)
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
            var artifactDirectoryName = SanitizePathPart(record.MethodName.Length > 0 ? record.MethodName : record.Name);
            if (artifactDirectoryName.Length == 0)
            {
                artifactDirectoryName = "test";
            }
            var baseArtifactDirectoryName = artifactDirectoryName;
            var directorySuffix = 2;
            while (!artifactDirectories.Add(artifactDirectoryName))
            {
                artifactDirectoryName = $"{baseArtifactDirectoryName}-{directorySuffix}";
                directorySuffix++;
            }
            var testArtifactDirectory = Path.Combine(artifactsRoot, artifactDirectoryName);
            Directory.CreateDirectory(testArtifactDirectory);

            File.WriteAllText(Path.Combine(testArtifactDirectory, "test-output.log"), record.Stdout, new UTF8Encoding(false));
            if (!string.IsNullOrEmpty(record.Stderr))
            {
                File.WriteAllText(Path.Combine(testArtifactDirectory, "test-error.log"), record.Stderr, new UTF8Encoding(false));
            }

            foreach (var sourcePath in resultFiles)
            {
                var destinationPath = Path.Combine(testArtifactDirectory, Path.GetFileName(sourcePath));
                if (File.Exists(destinationPath) && !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
                {
                    var extension = Path.GetExtension(destinationPath);
                    var name = Path.GetFileNameWithoutExtension(destinationPath);
                    var suffix = 2;
                    do
                    {
                        destinationPath = Path.Combine(testArtifactDirectory, $"{name}-{suffix}{extension}");
                        suffix++;
                    } while (File.Exists(destinationPath));
                }
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

            record.Artifacts = Directory.EnumerateFiles(testArtifactDirectory)
                .Order(StringComparer.Ordinal)
                .Select(path => Path.GetRelativePath(Path.GetDirectoryName(options.HtmlPath)!, path).Replace('\\', '/'))
                .ToList();
            catalog.Apply(record);
            execution.Tests.Add(record);
        }

        var times = root.Element(TrxNamespace + "Times");
        execution.TrxRunId = Attribute(root, "id");
        execution.TrxRunName = Attribute(root, "name");
        execution.TrxStartedUtc = Attribute(times, "start");
        execution.TrxCompletedUtc = Attribute(times, "finish");
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim('-');
        const int maxPathPartLength = 80;
        if (sanitized.Length <= maxPathPartLength)
        {
            return sanitized;
        }

        var hashBytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sanitized));
        var hash = Convert.ToHexString(hashBytes.AsSpan(0, 6)).ToLowerInvariant();
        var prefixLength = maxPathPartLength - hash.Length - 1;
        return $"{sanitized[..prefixLength].TrimEnd('-')}-{hash}";
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
