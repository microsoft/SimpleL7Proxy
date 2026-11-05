using System.IO;
using System.Text;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes Responses SSE deltas and closing events with finalized text.</summary>
internal sealed class ResponsesSSEOutput : LLMApiOutput {
    private int _sequenceNumber;
    private string? _messageId;
    private readonly StringBuilder _content = new();

    public ResponsesSSEOutput(Stream outputStream, string targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        _messageId = preamble.MessageId;
        foreach (string eventType in new[] { "response.created", "response.in_progress" }) {
            using var writer = StartEvent(System.Text.Encoding.UTF8.GetBytes($"event: {eventType}\ndata: "));
            writer.WriteStartObject();
            writer.WriteString("type", eventType);
            writer.WriteNumber("sequence_number", _sequenceNumber++);
            writer.WriteStartObject("response");
            if (preamble.ResponseId is string responseId) {
                writer.WriteString("id", responseId);
            }
            if (preamble.CreatedAt is long createdAt) {
                writer.WriteNumber("created_at", createdAt);
            }
            writer.WriteString("object", "response");
            writer.WriteString("model", TargetModel);
            writer.WriteString("status", "in_progress");
            writer.WriteStartArray("output");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        using (var writer = StartEvent("event: response.output_item.added\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "response.output_item.added");
            writer.WriteNumber("sequence_number", _sequenceNumber++);
            writer.WriteNumber("output_index", 0);
            writer.WriteStartObject("item");
            if (_messageId is not null) {
                writer.WriteString("id", _messageId);
            }
            writer.WriteString("type", "message");
            writer.WriteString("role", preamble.Role ?? "assistant");
            writer.WriteString("status", "in_progress");
            writer.WriteStartArray("content");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        using (var writer = StartEvent("event: response.content_part.added\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "response.content_part.added");
            writer.WriteNumber("sequence_number", _sequenceNumber++);
            writer.WriteNumber("output_index", 0);
            writer.WriteNumber("content_index", 0);
            if (_messageId is not null) {
                writer.WriteString("item_id", _messageId);
            }
            writer.WriteStartObject("part");
            writer.WriteString("type", "output_text");
            writer.WriteString("text", string.Empty);
            writer.WriteStartArray("annotations");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
    }

    public override void OutputData(string content) {
        _content.Append(content);
        using var writer = StartEvent("event: response.output_text.delta\ndata: "u8);
        writer.WriteStartObject();
        writer.WriteString("type", "response.output_text.delta");
        writer.WriteNumber("sequence_number", _sequenceNumber++);
        writer.WriteNumber("output_index", 0);
        writer.WriteNumber("content_index", 0);
        if (_messageId is not null) {
            writer.WriteString("item_id", _messageId);
        }
        writer.WriteString("delta", content);
        writer.WriteEndObject();
        CompleteEvent(writer);
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        string status = ResponseStatus(postamble);
        string itemStatus = status == "completed" ? "completed" : "incomplete";
        try {
            foreach (string eventType in new[] { "response.output_text.done", "response.content_part.done", "response.output_item.done", $"response.{status}" }) {
                using var writer = StartEvent(Encoding.UTF8.GetBytes($"event: {eventType}\ndata: "));
                writer.WriteStartObject();
                writer.WriteString("type", eventType);
                writer.WriteNumber("sequence_number", _sequenceNumber++);
                bool textDone = eventType == "response.output_text.done";
                bool partDone = eventType == "response.content_part.done";
                bool itemDone = eventType == "response.output_item.done";
                if (textDone || partDone || itemDone) {
                    writer.WriteNumber("output_index", 0);
                }
                if (textDone || partDone) {
                    writer.WriteNumber("content_index", 0);
                    writer.WriteString("item_id", _messageId ?? postamble.MessageId);
                }
                if (!textDone && !partDone && !itemDone) {
                    writer.WriteStartObject("response");
                    writer.WriteString("id", postamble.ResponseId ?? preamble.ResponseId);
                    if ((postamble.CreatedAt ?? preamble.CreatedAt) is long createdAt) {
                        writer.WriteNumber("created_at", createdAt);
                    }
                    writer.WriteString("object", "response");
                    writer.WriteString("model", TargetModel);
                    writer.WriteString("status", status);
                    WriteUsage(writer, postamble, LLMApiFormatEnum.OpenAIResponses);
                    if (status == "incomplete") {
                        writer.WriteStartObject("incomplete_details");
                        writer.WriteString("reason", postamble.FinishReason == "length" ? "max_output_tokens" : postamble.FinishReason);
                        writer.WriteEndObject();
                    }
                    writer.WriteStartArray("output");
                }
                if (!textDone && !partDone) {
                    if (itemDone) {
                        writer.WriteStartObject("item");
                    }
                    else {
                        writer.WriteStartObject();
                    }
                    writer.WriteString("id", _messageId ?? postamble.MessageId);
                    writer.WriteString("type", "message");
                    writer.WriteString("role", preamble.Role ?? "assistant");
                    writer.WriteString("status", itemStatus);
                    writer.WriteStartArray("content");
                }
                if (!textDone) {
                    if (partDone) {
                        writer.WriteStartObject("part");
                    }
                    else {
                        writer.WriteStartObject();
                    }
                    writer.WriteString("type", "output_text");
                    writer.WriteStartArray("annotations");
                    writer.WriteEndArray();
                }
                writer.WritePropertyName("text");
                foreach (ReadOnlyMemory<char> chunk in _content.GetChunks()) {
                    writer.WriteStringValueSegment(chunk.Span, false);
                }
                writer.WriteStringValueSegment(ReadOnlySpan<char>.Empty, true);
                writer.WriteStartArray("logprobs");
                writer.WriteEndArray();
                if (!textDone) {
                    writer.WriteEndObject();
                }
                if (!textDone && !partDone) {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (!textDone && !partDone && !itemDone) {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                CompleteEvent(writer);
            }
        }
        finally {
            _content.Clear();
            _content.Capacity = 0;
        }
    }
}