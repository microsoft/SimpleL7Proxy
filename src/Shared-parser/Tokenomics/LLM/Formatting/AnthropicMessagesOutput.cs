using System.IO;
using SharedParser.Tokenomics.LLM.Models;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes Anthropic Messages JSON text content.</summary>
internal sealed class AnthropicMessagesOutput : LLMApiOutput {
    public AnthropicMessagesOutput(Stream outputStream, ValidModel targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        var writer = StartJson();
        if ((preamble.MessageId ?? preamble.ResponseId) is string messageId) {
            writer.WriteString("id", messageId);
        }
        writer.WriteString("type", "message");
        writer.WriteString("role", preamble.Role ?? "assistant");
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WritePropertyName("text");
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        Writer!.WriteStringValueSegment(ReadOnlySpan<char>.Empty, true);
        Writer.WriteEndObject();
        Writer.WriteEndArray();
        Writer.WriteString("stop_reason", AnthropicStopReason(postamble));
        Writer.WriteString("stop_sequence", postamble.StopSequence);
        if (preamble.MessageId is null && preamble.ResponseId is null
            && (postamble.MessageId ?? postamble.ResponseId) is string messageId) {
            Writer.WriteString("id", messageId);
        }
        WriteUsage(Writer, postamble, LLMApiFormatEnum.AnthropicMessages);
        CompleteJson();
    }
}