namespace chat_tester.Components.Shared.EventHub.Pipeline;

public sealed class ServerPipelineProcessor : BasePipelineProcessor
{
    private static readonly string[] ServerKeys =
    {
        "ContainerApp",
        "Replica",
        "Date",
        "Timestamp",
        "Ver",
        "LoadBalanceMode",
        "ActiveHostsCount",
    };

    public ServerPipelineProcessor(Func<string, Dictionary<string, string>> parseEventData)
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

            foreach (var key in ServerKeys)
            {
                CaptureIfPresent(server, eventData, key);
            }
        }
    }
}
