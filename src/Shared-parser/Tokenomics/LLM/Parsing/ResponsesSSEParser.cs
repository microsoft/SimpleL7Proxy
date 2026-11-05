using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsers;

/// <summary>Parses Responses SSE responses.</summary>
public sealed class ResponsesSSEParser : LLMSSEApiParser {
    private bool _isTextEvent;
    private bool _isTextMessage;
    private bool _isMessageItem;
    private string? _messageId;
    private string? _messageRole;

    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        bool envelope = Containers.Count == 2 && Containers[1] == ("response", false);
        CaptureEnvelope(ref reader, envelope, false);
        if (Containers.Count != 2 || Containers[1] != ("item", false)
            || reader.TokenType != JsonTokenType.String) {
            return;
        }
        if (PropertyName == "id") {
            _messageId = reader.GetString();
        }
        else if (PropertyName == "role") {
            _messageRole = reader.GetString();
        }
        else if (PropertyName == "type") {
            _isMessageItem = reader.ValueTextEquals("message");
        }
        if (_isMessageItem) {
            Preamble.MessageId = _messageId ?? Preamble.MessageId;
            Preamble.Role = _messageRole ?? Preamble.Role;
        }
    }

    protected override void ReleasePreambleState() {
        _messageId = null;
        _messageRole = null;
        _isMessageItem = false;
    }

    protected override bool IsContentContext => _isTextEvent || _isTextMessage;

    protected override void ProcessEvent(ReadOnlySpan<char> value) {
        _isTextEvent = value.SequenceEqual("response.output_text.delta");
        if (value.SequenceEqual("response.completed") || value.SequenceEqual("response.failed")
            || value.SequenceEqual("response.incomplete")) {
            Postamble.Status = value[9..].ToString();
            IsTerminalEvent = true;
            EndData();
        }
    }

    protected override void ObserveStringValue(ref Utf8JsonReader reader) {
        if (Containers.Count == 1 && PropertyName == "type") {
            _isTextMessage = reader.ValueTextEquals("response.output_text.delta");
            if (reader.ValueTextEquals("response.completed") || reader.ValueTextEquals("response.failed")
                || reader.ValueTextEquals("response.incomplete")) {
                Postamble.Status = reader.GetString()![9..];
                IsTerminalEvent = true;
                EndData();
            }
        }
    }

    protected override void ResetJsonContext() {
        _isTextMessage = false;
        ReleasePreambleState();
    }

    protected override bool IsContentValue(ref Utf8JsonReader reader) =>
        PropertyName == "delta" && Containers.Count == 1 && !Containers[0].IsArray;
}