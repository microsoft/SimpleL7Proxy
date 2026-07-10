namespace chat_tester.Components.Shared.EventHub.Pipeline;

public sealed class BackendPipelineProcessor : BasePipelineProcessor
{
    public BackendPipelineProcessor(Func<string, Dictionary<string, string>> parseEventData)
        : base(parseEventData)
    {
    }

    public override void Process(
        Dictionary<string, string> server,
        Dictionary<string, string> backend,
        Dictionary<string, string> endpoint,
        Dictionary<string, string> requests,
        string[] incomingRecords)
    {
        foreach (var incomingRecord in incomingRecords)
        {
            if (!TryParseRecord(incomingRecord, out var eventData) || eventData is null)
            {
                continue;
            }

            if (!string.Equals(GetValue(eventData, "Type"), "S7P-Backend", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var pair in eventData)
            {
                backend[pair.Key] = pair.Value;
            }
        }
    }
}
