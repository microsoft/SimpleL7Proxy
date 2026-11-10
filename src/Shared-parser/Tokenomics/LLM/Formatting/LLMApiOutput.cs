using System.Buffers;
using System.IO;
using System.Text.Json;
using SharedParser.Tokenomics.LLM.Catalog;
using SharedParser.Tokenomics.LLM.Models;

namespace SharedParser.Tokenomics.LLM.Formatting;

/// <summary>Writes generated text in a selected target API format.</summary>
internal abstract class LLMApiOutput {
    protected readonly Stream OutputStream;
    protected readonly ValidModel TargetModel;
    protected Utf8JsonWriter? Writer;
    private readonly ArrayBufferWriter<byte> _buffer = new();

    protected LLMApiOutput(Stream outputStream, ValidModel targetModel) {
        OutputStream = outputStream;
        TargetModel = targetModel;
    }

    internal static LLMApiOutput Create(ValidModel targetModel, bool streaming, Stream outputStream) => (targetModel.ApiFormat, streaming) switch {
        (LLMApiEnum.OpenAIChatCompletions, false) => new CompletionOutput(outputStream, targetModel),
        (LLMApiEnum.OpenAIChatCompletions, true) => new CompletionSSEOutput(outputStream, targetModel),
        (LLMApiEnum.OpenAIResponses, false) => new ResponsesOutput(outputStream, targetModel),
        (LLMApiEnum.OpenAIResponses, true) => new ResponsesSSEOutput(outputStream, targetModel),
        (LLMApiEnum.AnthropicMessages, false) => new AnthropicMessagesOutput(outputStream, targetModel),
        (LLMApiEnum.AnthropicMessages, true) => new AnthropicMessagesSSEOutput(outputStream, targetModel),
        _ => throw new NotSupportedException("No writer is registered for the validated target API format.")
    };

    public abstract void OutputPre(LLMPreamble preamble);

    public virtual void OutputData(NonNullString content) => Writer!.WriteStringValueSegment(content.Value.AsSpan(), false);

    public virtual void DrainSerializedBytes() {
        Writer?.Flush();
        if (_buffer.WrittenCount > 0) {
            OutputStream.Write(_buffer.WrittenSpan);
            _buffer.ResetWrittenCount();
        }
    }

    public async ValueTask DrainSerializedBytesAsync() {
        Writer?.Flush();
        if (_buffer.WrittenCount > 0) {
            await OutputStream.WriteAsync(_buffer.WrittenMemory).ConfigureAwait(false);
            _buffer.ResetWrittenCount();
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

    protected static void WriteUsage(Utf8JsonWriter writer, LLMPostamble postamble, LLMApiEnum format) {
        if (postamble.InputTokens is null && postamble.OutputTokens is null
            && postamble.TotalTokens is null && postamble.CachedInputTokens is null
            && postamble.CacheCreationInputTokens is null && postamble.ReasoningOutputTokens is null) {
            return;
        }
        writer.WriteStartObject("usage");
        if (postamble.InputTokens is long input) {
            writer.WriteNumber(format == LLMApiEnum.OpenAIChatCompletions ? "prompt_tokens" : "input_tokens", input);
        }
        if (postamble.OutputTokens is long output) {
            writer.WriteNumber(format == LLMApiEnum.OpenAIChatCompletions ? "completion_tokens" : "output_tokens", output);
        }
        if (format != LLMApiEnum.AnthropicMessages) {
            long? total = postamble.TotalTokens ?? (postamble.InputTokens + postamble.OutputTokens);
            if (total is long totalTokens) {
                writer.WriteNumber("total_tokens", totalTokens);
            }
            if (postamble.CachedInputTokens is long cached) {
                writer.WriteStartObject(format == LLMApiEnum.OpenAIChatCompletions ? "prompt_tokens_details" : "input_tokens_details");
                writer.WriteNumber("cached_tokens", cached);
                writer.WriteEndObject();
            }
            if (postamble.ReasoningOutputTokens is long reasoning) {
                writer.WriteStartObject(format == LLMApiEnum.OpenAIChatCompletions ? "completion_tokens_details" : "output_tokens_details");
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
        Writer.WriteString("model", TargetModel.Name.Value);
        return Writer;
    }

    protected void CompleteJson() {
        Writer!.WriteEndObject();
        Writer.Dispose();
        Writer = null;
    }

    protected Utf8JsonWriter StartEvent(ReadOnlySpan<byte> prefix) {
        WriteFraming(prefix);
        return new Utf8JsonWriter(_buffer);
    }

    protected void CompleteEvent(Utf8JsonWriter writer) {
        writer.Dispose();
        WriteFraming("\n\n"u8);
    }

    protected void WriteFraming(ReadOnlySpan<byte> bytes) {
        bytes.CopyTo(_buffer.GetSpan(bytes.Length));
        _buffer.Advance(bytes.Length);
    }
}