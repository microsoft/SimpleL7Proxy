using System.Text.Json;

namespace chat_tester.Components.Shared.EventHub.Pipeline;

public sealed class CleanupProcessor : BasePipelineProcessor
{
    private readonly Func<string, bool> _processIncomingRecord;
    private readonly Action<Exception, string> _logInvalidRecord;
    private readonly Action<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, string[]> _publishMetrics;

    public CleanupProcessor(
        Func<string, bool> processIncomingRecord,
        Action<Exception, string> logInvalidRecord,
        Action<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, string[]> publishMetrics)
        : base()
    {
        _processIncomingRecord = processIncomingRecord;
        _logInvalidRecord = logInvalidRecord;
        _publishMetrics = publishMetrics;
    }

    public PipelineRunResult Finalize(
        Dictionary<string, string> server,
        Dictionary<string, string> backend,
        Dictionary<string, string> endpoint,
        Dictionary<string, string> requests,
        string[] incomingRecords,
        string source)
    {
        Process(server, backend, endpoint, requests, incomingRecords);

        var processedCount = 0;
        var skippedCount = 0;

        foreach (var incomingRecord in incomingRecords)
        {
            try
            {
                if (_processIncomingRecord(incomingRecord))
                {
                    processedCount++;
                }
                else
                {
                    skippedCount++;
                }
            }
            catch (JsonException ex)
            {
                skippedCount++;
                _logInvalidRecord(ex, source);
            }
        }

        return new PipelineRunResult(processedCount, skippedCount);
    }

    public override void Process(
        Dictionary<string, string> server,
        Dictionary<string, string> backend,
        Dictionary<string, string> endpoint,
        Dictionary<string, string> requests,
        string[] incomingRecords)
    {
        _publishMetrics(server, backend, endpoint, requests, incomingRecords);

        // Final pipeline stage can use all collected dictionaries to perform
        // normalization/finalization before store updates are applied.
        if (server.Count == 0 && backend.Count == 0 && endpoint.Count == 0 && requests.Count == 0)
        {
            return;
        }
    }
}
