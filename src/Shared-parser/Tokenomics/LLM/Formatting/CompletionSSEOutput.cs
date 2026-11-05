using System.IO;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes Chat Completions SSE text deltas and final metadata.</summary>
internal sealed class CompletionSSEOutput : LLMApiOutput {
    private LLMPreamble _preamble = new();

    public CompletionSSEOutput(Stream outputStream, string targetModel) : base(outputStream, targetModel) { }

    public override void OutputPre(LLMPreamble preamble) {
        _preamble = preamble;
        using var writer = StartEvent("data: "u8);
        writer.WriteStartObject();
        if ((preamble.ResponseId ?? preamble.MessageId) is string responseId) {
            writer.WriteString("id", responseId);
        }
        if (preamble.CreatedAt is long createdAt) {
            writer.WriteNumber("created", createdAt);
        }
        writer.WriteString("model", TargetModel);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta");
        writer.WriteString("role", preamble.Role ?? "assistant");
        writer.WriteString("content", string.Empty);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        CompleteEvent(writer);
    }

    public override void OutputData(string content) {
        using var writer = StartEvent("data: "u8);
        writer.WriteStartObject();
        if ((_preamble.ResponseId ?? _preamble.MessageId) is string responseId) {
            writer.WriteString("id", responseId);
        }
        if (_preamble.CreatedAt is long createdAt) {
            writer.WriteNumber("created", createdAt);
        }
        writer.WriteString("model", TargetModel);
        writer.WriteString("object", "chat.completion.chunk");
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta");
        writer.WriteString("content", content);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        CompleteEvent(writer);
    }

    public override void OutputPost(LLMPreamble preamble, LLMPostamble postamble) {
        using (var writer = StartEvent("data: "u8)) {
            writer.WriteStartObject();
            writer.WriteString("id", postamble.ResponseId ?? preamble.ResponseId ?? preamble.MessageId);
            if ((postamble.CreatedAt ?? preamble.CreatedAt) is long createdAt) {
                writer.WriteNumber("created", createdAt);
            }
            writer.WriteString("model", TargetModel);
            writer.WriteString("object", "chat.completion.chunk");
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("delta");
            writer.WriteEndObject();
            writer.WriteString("finish_reason", postamble.FinishReason ?? "stop");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        if (postamble.InputTokens is not null || postamble.OutputTokens is not null || postamble.TotalTokens is not null) {
            using var writer = StartEvent("data: "u8);
            writer.WriteStartObject();
            writer.WriteString("id", postamble.ResponseId ?? preamble.ResponseId ?? preamble.MessageId);
            if ((postamble.CreatedAt ?? preamble.CreatedAt) is long createdAt) {
                writer.WriteNumber("created", createdAt);
            }
            writer.WriteString("model", TargetModel);
            writer.WriteString("object", "chat.completion.chunk");
            writer.WriteStartArray("choices");
            writer.WriteEndArray();
            WriteUsage(writer, postamble, LLMApiFormatEnum.OpenAIChatCompletions);
            writer.WriteEndObject();
            CompleteEvent(writer);
        }
        OutputStream.Write("data: [DONE]\n\n"u8);
    }
}