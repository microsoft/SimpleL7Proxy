using System.Buffers;
using System.IO;
using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes generated text in a selected target API format.</summary>
internal abstract class LLMApiOutput {
    protected readonly Stream OutputStream;
    protected readonly string TargetModel;
    protected Utf8JsonWriter? Writer;
    private readonly ArrayBufferWriter<byte> _buffer = new();

    protected LLMApiOutput(Stream outputStream, string targetModel) {
        OutputStream = outputStream;
        TargetModel = targetModel;
    }

    internal static LLMApiOutput Create(LLMApiFormatEnum format, bool streaming, Stream outputStream, string targetModel) => (format, streaming) switch {
        (LLMApiFormatEnum.OpenAIChatCompletions, false) => new CompletionOutput(outputStream, targetModel),
        (LLMApiFormatEnum.OpenAIChatCompletions, true) => new CompletionSSEOutput(outputStream, targetModel),
        (LLMApiFormatEnum.OpenAIResponses, false) => new ResponsesOutput(outputStream, targetModel),
        (LLMApiFormatEnum.OpenAIResponses, true) => new ResponsesSSEOutput(outputStream, targetModel),
        (LLMApiFormatEnum.AnthropicMessages, false) => new AnthropicMessagesOutput(outputStream, targetModel),
        (LLMApiFormatEnum.AnthropicMessages, true) => new AnthropicMessagesSSEOutput(outputStream, targetModel),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported target API format.")
    };

    public abstract void OutputPre(LLMPreamble preamble);

    public virtual void OutputData(string content) => Writer!.WriteStringValueSegment(content.AsSpan(), false);

    public virtual void DrainSerializedBytes() {
        Writer?.Flush();
        if (_buffer.WrittenCount > 0) {
            OutputStream.Write(_buffer.WrittenSpan);
            _buffer.Clear();
        }
    }

    public abstract void OutputPost(LLMPreamble preamble, LLMPostamble postamble);

    protected static string ResponseStatus(LLMPostamble postamble) => postamble.Status
        ?? (postamble.FinishReason is "length" or "content_filter" ? "incomplete" : "completed");

    protected static string AnthropicStopReason(LLMPostamble postamble) => postamble.FinishReason switch {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        "stop" or null => postamble.StopSequence is null ? "end_turn" : "stop_sequence",
        _ => postamble.FinishReason
    };

    protected static void WriteUsage(Utf8JsonWriter writer, LLMPostamble postamble, LLMApiFormatEnum format) {
        if (postamble.InputTokens is null && postamble.OutputTokens is null
            && postamble.TotalTokens is null && postamble.CachedInputTokens is null
            && postamble.CacheCreationInputTokens is null && postamble.ReasoningOutputTokens is null) {
            return;
        }
        writer.WriteStartObject("usage");
        if (postamble.InputTokens is long input) {
            writer.WriteNumber(format == LLMApiFormatEnum.OpenAIChatCompletions ? "prompt_tokens" : "input_tokens", input);
        }
        if (postamble.OutputTokens is long output) {
            writer.WriteNumber(format == LLMApiFormatEnum.OpenAIChatCompletions ? "completion_tokens" : "output_tokens", output);
        }
        if (format != LLMApiFormatEnum.AnthropicMessages) {
            long? total = postamble.TotalTokens ?? (postamble.InputTokens + postamble.OutputTokens);
            if (total is long totalTokens) {
                writer.WriteNumber("total_tokens", totalTokens);
            }
            if (postamble.CachedInputTokens is long cached) {
                writer.WriteStartObject(format == LLMApiFormatEnum.OpenAIChatCompletions ? "prompt_tokens_details" : "input_tokens_details");
                writer.WriteNumber("cached_tokens", cached);
                writer.WriteEndObject();
            }
            if (postamble.ReasoningOutputTokens is long reasoning) {
                writer.WriteStartObject(format == LLMApiFormatEnum.OpenAIChatCompletions ? "completion_tokens_details" : "output_tokens_details");
                writer.WriteNumber("reasoning_tokens", reasoning);
                writer.WriteEndObject();
            }
        }
        else {
            if (postamble.CachedInputTokens is long cached) {
                writer.WriteNumber("cache_read_input_tokens", cached);
            }
            if (postamble.CacheCreationInputTokens is long cacheCreation) {
                writer.WriteNumber("cache_creation_input_tokens", cacheCreation);
            }
        }
        writer.WriteEndObject();
    }

    protected Utf8JsonWriter StartJson() {
        Writer = new Utf8JsonWriter(_buffer);
        Writer.WriteStartObject();
        Writer.WriteString("model", TargetModel);
        return Writer;
    }

    protected void CompleteJson() {
        Writer!.WriteEndObject();
        Writer.Dispose();
        Writer = null;
        DrainSerializedBytes();
    }

    protected Utf8JsonWriter StartEvent(ReadOnlySpan<byte> prefix) {
        OutputStream.Write(prefix);
        return new Utf8JsonWriter(_buffer);
    }

    protected void CompleteEvent(Utf8JsonWriter writer) {
        writer.Dispose();
        DrainSerializedBytes();
        OutputStream.Write("\n\n"u8);
    }
}