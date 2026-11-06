using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsing;

/// <summary>Parses Responses JSON responses.</summary>
public sealed class ResponsesParser : LLMApiParser {
    private int _itemIndex = -1;
    private int _messageIndex = -1;
    private string? _messageId;
    private string? _messageRole;

    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        CaptureEnvelope(ref reader, Containers.Count == 1 && !Containers[0].IsArray, false);
        if (Containers.Count != 3 || Containers[1] != ("output", true)
            || Containers[2].IsArray || reader.TokenType != JsonTokenType.String) {
            return;
        }
        if (_itemIndex != CollectionItemIndex) {
            ReleasePreambleState();
            _itemIndex = CollectionItemIndex;
        }
        if (PropertyName == "id") {
            _messageId = reader.GetString();
        }
        else if (PropertyName == "role") {
            _messageRole = reader.GetString();
        }
        else if (PropertyName == "type" && reader.ValueTextEquals("message") && _messageIndex < 0) {
            _messageIndex = CollectionItemIndex;
        }
        if (_messageIndex == CollectionItemIndex) {
            Preamble.MessageId = _messageId ?? Preamble.MessageId;
            Preamble.Role = _messageRole ?? Preamble.Role;
        }
    }

    protected override void ReleasePreambleState() {
        _messageId = null;
        _messageRole = null;
    }

    protected override bool IsContentValue(ref Utf8JsonReader reader) =>
        PropertyName == "text" && Containers.Count == 5 && !Containers[0].IsArray
        && Containers[1] == ("output", true) && !Containers[2].IsArray
        && Containers[3] == ("content", true) && !Containers[4].IsArray;
}