using System.IO;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes Anthropic Messages SSE text deltas and final metadata.</summary>
internal sealed class AnthropicMessagesSSEOutput : LLMApiOutput {
    public AnthropicMessagesSSEOutput(Stream outputStream, string targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        using (var writer = StartEvent("event: message_start\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "message_start");
            writer.WriteStartObject("message");
            if ((preamble.MessageId ?? preamble.ResponseId) is string messageId) {
                writer.WriteString("id", messageId);
            }
            writer.WriteString("type", "message");
            writer.WriteString("model", TargetModel);
            writer.WriteString("role", preamble.Role ?? "assistant");
            writer.WriteStartArray("content");
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        using (var writer = StartEvent("event: content_block_start\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "content_block_start");
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("content_block");
            writer.WriteString("type", "text");
            writer.WriteString("text", string.Empty);
            writer.WriteEndObject();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
    }

    public override void OutputData(string content) {
        using var writer = StartEvent("event: content_block_delta\ndata: "u8);
        writer.WriteStartObject();
        writer.WriteString("type", "content_block_delta");
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta");
        writer.WriteString("type", "text_delta");
        writer.WriteString("text", content);
        writer.WriteEndObject();
        writer.WriteEndObject();
        CompleteEvent(writer);
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        using (var writer = StartEvent("event: content_block_stop\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "content_block_stop");
            writer.WriteNumber("index", 0);
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        using (var writer = StartEvent("event: message_delta\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "message_delta");
            writer.WriteStartObject("delta");
            writer.WriteString("stop_reason", AnthropicStopReason(postamble));
            writer.WriteString("stop_sequence", postamble.StopSequence);
            writer.WriteEndObject();
            WriteUsage(writer, postamble, LLMApiFormatEnum.AnthropicMessages);
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        using (var writer = StartEvent("event: message_stop\ndata: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("type", "message_stop");
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
    }
}