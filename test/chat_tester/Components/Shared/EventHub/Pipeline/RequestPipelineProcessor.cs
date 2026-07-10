namespace chat_tester.Components.Shared.EventHub.Pipeline;

public sealed class RequestPipelineProcessor : BasePipelineProcessor
{
    public RequestPipelineProcessor(Func<string, Dictionary<string, string>> parseEventData)
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

            var correlationKey = GetCorrelationKey(eventData);
            if (string.IsNullOrWhiteSpace(correlationKey))
            {
                continue;
            }

            var eventType = GetValue(eventData, "Type");
            var status = GetValue(eventData, "Status");
            var path = GetValue(eventData, "Path");
            requests[correlationKey] = string.Join(
                "|",
                new[] { eventType, status, path }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
}
