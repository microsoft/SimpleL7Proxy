using System.Text.Json;

namespace chat_tester.Components.Shared.EventHub.Pipeline;

public abstract class BasePipelineProcessor
{
    private readonly Func<string, Dictionary<string, string>>? _parseEventData;

    protected BasePipelineProcessor(Func<string, Dictionary<string, string>>? parseEventData = null)
    {
        _parseEventData = parseEventData;
    }

    public void Register(ICollection<BasePipelineProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        processors.Add(this);
    }

    public abstract void Process(
        Dictionary<string, string> server,
        Dictionary<string, string> backend,
        Dictionary<string, string> endpoint,
        Dictionary<string, string> requests,
        string[] incomingRecords);

    protected bool TryParseRecord(string incomingRecord, out Dictionary<string, string>? eventData)
    {
        if (_parseEventData is null)
        {
            eventData = null;
            return false;
        }

        try
        {
            eventData = _parseEventData(incomingRecord);
            return true;
        }
        catch (JsonException)
        {
            eventData = null;
            return false;
        }
    }

    protected static void CaptureIfPresent(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, string> source,
        string key)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    protected static string GetValue(IReadOnlyDictionary<string, string> eventData, string key)
    {
        return eventData.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : string.Empty;
    }

    protected static string? GetCorrelationKey(IReadOnlyDictionary<string, string> eventData)
    {
        return FirstNonEmpty(
            GetValue(eventData, "MID"),
            GetValue(eventData, "GUID"));
    }

    protected static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
