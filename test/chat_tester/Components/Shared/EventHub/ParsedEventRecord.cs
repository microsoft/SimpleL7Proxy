namespace chat_tester.Components.Shared.EventHub;

/// <summary>
/// A single Event Hub record parsed exactly once into a case-insensitive field map, carried
/// through the ingest path so no stage re-parses the raw JSON. <see cref="Raw"/> is retained
/// for length-based metrics and diagnostics.
/// </summary>
public sealed record ParsedEventRecord(string Raw, IReadOnlyDictionary<string, string> Data)
{
    public string Type => EventFields.Get(Data, "Type");
    public string ContainerApp => EventFields.Get(Data, "ContainerApp");
    public string Replica => EventFields.Get(Data, "Replica");
}
