namespace chat_tester.Components.Shared.EventHub.Pipeline;

public sealed class EndpointPipelineProcessor : BasePipelineProcessor
{
    public EndpointPipelineProcessor(Func<string, Dictionary<string, string>> parseEventData)
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

            CaptureIfPresent(endpoint, eventData, "Method");
            CaptureIfPresent(endpoint, eventData, "Path");
            CaptureIfPresent(endpoint, eventData, "Uri");
            CaptureIfPresent(endpoint, eventData, "RequestType");
            CaptureIfPresent(endpoint, eventData, "RequestHost");
        }
    }
}
