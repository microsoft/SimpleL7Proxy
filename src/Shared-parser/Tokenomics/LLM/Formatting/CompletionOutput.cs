using System.IO;

namespace SharedParser.Tokenomics.LLM.Formaters;

/// <summary>Writes Chat Completions JSON text content.</summary>
internal sealed class CompletionOutput : LLMApiOutput {
    public CompletionOutput(Stream outputStream, string targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        var writer = StartJson();
        if ((preamble.ResponseId ?? preamble.MessageId) is string responseId) {
            writer.WriteString("id", responseId);
        }
        if (preamble.CreatedAt is long createdAt) {
            writer.WriteNumber("created", createdAt);
        }
        writer.WriteString("object", "chat.completion");
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("message");
        writer.WriteString("role", preamble.Role ?? "assistant");
        writer.WritePropertyName("content");
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        Writer!.WriteStringValueSegment(ReadOnlySpan<char>.Empty, true);
        Writer.WriteEndObject();
        Writer.WriteString("finish_reason", postamble.FinishReason ?? "stop");
        Writer.WriteEndObject();
        Writer.WriteEndArray();
        if (preamble.ResponseId is null && preamble.MessageId is null && postamble.ResponseId is string responseId) {
            Writer.WriteString("id", responseId);
        }
        if (preamble.CreatedAt is null && postamble.CreatedAt is long createdAt) {
            Writer.WriteNumber("created", createdAt);
        }
        WriteUsage(Writer, postamble, LLMApiFormatEnum.OpenAIChatCompletions);
        CompleteJson();
    }
}