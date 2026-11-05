using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsing;

/// <summary>Parses Anthropic Messages SSE responses.</summary>
public sealed class AnthropicMessagesSSEParser : LLMSSEApiParser {
    private bool _isTextEvent;
    private bool _isTextMessage;

    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        bool message = Containers.Count == 2 && Containers[1] == ("message", false);
        CaptureEnvelope(ref reader, message, message);
    }

    protected override bool IsContentContext => _isTextEvent || _isTextMessage;

    protected override void ProcessEvent(ReadOnlySpan<char> value) {
        _isTextEvent = value.SequenceEqual("content_block_delta");
        if (value.SequenceEqual("message_delta") || value.SequenceEqual("message_stop")) {
            EndData();
        }
        if (value.SequenceEqual("message_stop")) {
            IsTerminalEvent = true;
        }
    }

    protected override void ObserveStringValue(ref Utf8JsonReader reader) {
        if (Containers.Count == 1 && PropertyName == "type") {
            _isTextMessage = reader.ValueTextEquals("content_block_delta");
            if (reader.ValueTextEquals("message_delta") || reader.ValueTextEquals("message_stop")) {
                EndData();
            }
            if (reader.ValueTextEquals("message_stop")) {
                IsTerminalEvent = true;
            }
        }
    }

    protected override void ResetJsonContext() => _isTextMessage = false;

    protected override bool IsContentValue(ref Utf8JsonReader reader) =>
        PropertyName == "text" && Containers.Count == 2 && !Containers[0].IsArray
        && Containers[1] == ("delta", false);
}