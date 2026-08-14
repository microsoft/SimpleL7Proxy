using System.Globalization;

namespace RegressionReportRenderer;

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
            ManifestPath = Required("--manifest"),
            HtmlPath = Required("--html"),
            LandingPath = Required("--landing"),
            HistoryRootPath = Required("--history-root"),
            MasterRunId = Text("--master-run-id"),
            ExecutionId = Text("--execution-id"),
            Label = Text("--label"),
            TrxPath = Required("--trx"),
            ConsoleLogPath = Required("--console-log"),
            TestAssemblyPath = Required("--test-assembly"),
            ExitCode = int.Parse(Text("--exit-code"), CultureInfo.InvariantCulture),
            Command = Text("--command"),
            StartedUtc = Text("--started-utc"),
            CompletedUtc = Text("--completed-utc")
        };
    }
}
