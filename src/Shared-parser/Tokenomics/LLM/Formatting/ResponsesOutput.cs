using System.IO;

namespace SharedParser.Tokenomics.LLM.Formaters;

/// <summary>Writes Responses JSON output text.</summary>
internal sealed class ResponsesOutput : LLMApiOutput {
    public ResponsesOutput(Stream outputStream, string targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        var writer = StartJson();
        if (preamble.ResponseId is string responseId) {
            writer.WriteString("id", responseId);
        }
        if (preamble.CreatedAt is long createdAt) {
            writer.WriteNumber("created_at", createdAt);
        }
        writer.WriteString("object", "response");
        writer.WriteStartArray("output");
        writer.WriteStartObject();
        if (preamble.MessageId is string messageId) {
            writer.WriteString("id", messageId);
        }
        writer.WriteString("type", "message");
        writer.WriteString("role", preamble.Role ?? "assistant");
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "output_text");
        writer.WritePropertyName("text");
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        Writer!.WriteStringValueSegment(ReadOnlySpan<char>.Empty, true);
        Writer.WriteStartArray("annotations");
        Writer.WriteEndArray();
        Writer.WriteEndObject();
        Writer.WriteEndArray();
        Writer.WriteString("status", ResponseStatus(postamble) == "completed" ? "completed" : "incomplete");
        if (preamble.MessageId is null && postamble.MessageId is string messageId) {
            Writer.WriteString("id", messageId);
        }
        Writer.WriteEndObject();
        Writer.WriteEndArray();
        Writer.WriteString("status", ResponseStatus(postamble));
        if (preamble.ResponseId is null && postamble.ResponseId is string responseId) {
            Writer.WriteString("id", responseId);
        }
        if (preamble.CreatedAt is null && postamble.CreatedAt is long createdAt) {
            Writer.WriteNumber("created_at", createdAt);
        }
        if (ResponseStatus(postamble) == "incomplete") {
            Writer.WriteStartObject("incomplete_details");
            Writer.WriteString("reason", postamble.FinishReason == "length" ? "max_output_tokens" : postamble.FinishReason);
            Writer.WriteEndObject();
        }
        WriteUsage(Writer, postamble, LLMApiFormatEnum.OpenAIResponses);
        CompleteJson();
    }
}