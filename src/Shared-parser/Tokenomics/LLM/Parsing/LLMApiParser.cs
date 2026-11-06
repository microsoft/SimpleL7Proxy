#define LLM_UTF8_TRYGETBYTES
//#define LLM_SKIP_UNUSED_STRING_DECODING

using System.Text;
using System.Text.Json;
using SharedParser.Tokenomics.LLM.Models;

using SharedParser.Tokenomics.LLM.Catalog;

namespace SharedParser.Tokenomics.LLM.Parsing;

/// <summary>Parses an API response through its preamble, data, and post phases.</summary>
public abstract class LLMApiParser {
    /// <summary>Processes a line and returns the processor for the next line.</summary>
    public delegate LineProcessor LineProcessor(NonNullString line);

    /// <summary>Gets the current processing state.</summary>
    public LLMParserStateEnum ParserState { get; private set; } = LLMParserStateEnum.Pre;
    /// <summary>Gets the zero-based input line containing the first content value.</summary>
    public int ContentLineIndex { get; private set; } = -1;
    /// <summary>Gets the UTF-16 offset immediately after the content value's opening quote.</summary>
    public int ContentStartOffset { get; private set; } = -1;

    /// <summary>Gets the structured source preamble collected before data output starts.</summary>
    public LLMPreamble Preamble { get; } = new();

    /// <summary>Gets final metadata collected throughout the source response.</summary>
    public LLMPostamble Postamble { get; } = new();
    /// <summary>Gets whether a complete source response has been parsed.</summary>
    public bool IsComplete { get; protected set; }

    internal string[] PreBuffer => _capturePreLines ? _preSnapshot ??= _preLines.ToArray() : Array.Empty<string>();

    internal int CollectionItemIndex { get; private set; } = -1;
    protected List<(string Name, bool IsArray)> Containers { get; } = new();
    protected string PropertyName { get; private set; } = string.Empty;
    protected virtual bool IsContentContext => true;
    protected virtual bool IsContentCollectionEnd => Containers.Count == 2 && Containers[^1].IsArray;
    protected virtual bool CompletesAtRoot => true;
    protected Action<NonNullString>? ContentWriter => ParserState == LLMParserStateEnum.Data ? WriteContent : null;

    private readonly List<string> _preLines = new();
    private bool _capturePreLines;
    private string[]? _preSnapshot;
    private readonly LineProcessor _parsePre;
    private readonly LineProcessor _parseData;
    private readonly LineProcessor _parsePost;
    private Action<NonNullString>? _output;
    private byte[] _pendingBytes = new byte[4096];
    private int _pendingCount;
    private JsonReaderState _readerState;
    private int _lineIndex = -1;
    private int _candidateLineIndex = -1;
    private int _candidateOffset = -1;
    private string? _candidateContent;

    /// <summary>Initializes the cached phase processors.</summary>
    protected LLMApiParser() {
        _parsePre = ParsePre;
        _parseData = ParseData;
        _parsePost = ParsePost;
    }

    internal static LLMApiParser Create(ValidModel sourceModel, bool streaming, bool capturePreLines = false) {
        LLMApiParser parser = (sourceModel.ApiFormat, streaming) switch {
            (LLMApiEnum.OpenAIChatCompletions, false) => new CompletionParser(),
            (LLMApiEnum.OpenAIChatCompletions, true) => new CompletionSSEParser(),
            (LLMApiEnum.OpenAIResponses, false) => new ResponsesParser(),
            (LLMApiEnum.OpenAIResponses, true) => new ResponsesSSEParser(),
            (LLMApiEnum.AnthropicMessages, false) => new AnthropicMessagesParser(),
            (LLMApiEnum.AnthropicMessages, true) => new AnthropicMessagesSSEParser(),
        _ => throw new NotSupportedException("No parser is registered for the validated source API format.")
        };
        parser._capturePreLines = capturePreLines;
        return parser;
    }

    /// <summary>Processes one physical line without its line terminator.</summary>
    public LineProcessor ParsePre(NonNullString line) {
        if (ParserState != LLMParserStateEnum.Pre) {
            throw new InvalidOperationException("The parser is not in the pre phase.");
        }
        if (_capturePreLines) {
            _preLines.Add(line.Value);
            _preSnapshot = null;
        }
        _lineIndex++;
        ProcessPayload(line);
        if (ParserState != LLMParserStateEnum.Data) {
            return _parsePre;
        }

        ReleasePreambleState();
        if (_capturePreLines) {
            _preLines[ContentLineIndex] = _preLines[ContentLineIndex][..ContentStartOffset];
            _preLines.RemoveRange(ContentLineIndex + 1, _preLines.Count - ContentLineIndex - 1);
            _preSnapshot = _preLines.ToArray();
            _preLines.Clear();
            _preLines.TrimExcess();
        }
        return _parseData;
    }

    internal LineProcessor ResumeData(Action<NonNullString> writeContent) {
        ArgumentNullException.ThrowIfNull(writeContent);
        if (ParserState != LLMParserStateEnum.Data || _candidateContent is null) {
            throw new InvalidOperationException("No content is available for the data handoff.");
        }

        _output = writeContent;
        string content = _candidateContent;
        _candidateContent = null;
        _candidateLineIndex = -1;
        _candidateOffset = -1;
        WriteContent(new NonNullString(content));
        ProcessJson(ReadOnlySpan<char>.Empty, 0, writeContent: WriteContent);
        Array.Resize(ref _pendingBytes, _pendingCount);
        return ParserState == LLMParserStateEnum.Post ? _parsePost : _parseData;
    }

    /// <summary>Parses generated content and returns the next phase processor.</summary>
    public LineProcessor ParseData(NonNullString line) {
        if (ParserState != LLMParserStateEnum.Data || _output is null || _candidateContent is not null) {
            throw new InvalidOperationException("The data handoff must be completed before parsing data.");
        }
        ProcessPayload(line);
        return ParserState == LLMParserStateEnum.Post ? _parsePost : _parseData;
    }

    protected virtual void ProcessPayload(NonNullString line) => ProcessJson(line.Value.AsSpan(), 0, writeContent: ContentWriter);

    /// <summary>Parses final metadata and returns the post processor.</summary>
    public LineProcessor ParsePost(NonNullString line) {
        if (ParserState != LLMParserStateEnum.Post) {
            throw new InvalidOperationException("The parser is not in the post phase.");
        }
        ProcessPayload(line);
        return _parsePost;
    }

    /// <summary>Validates EOF without treating a blank input line as completion.</summary>
    public virtual void Complete() {
        if (!CompletesAtRoot && Postamble.Status == "failed") {
            throw new IOException("The source response stream failed.");
        }
        try {
            if (CompletesAtRoot) {
                ProcessJson(ReadOnlySpan<char>.Empty, 0, true, ContentWriter);
            }
        }
        catch (JsonException exception) {
            throw new EndOfStreamException("The source response ended before its JSON was complete.", exception);
        }
        if (!IsComplete || Containers.Count != 0 || _pendingCount != 0) {
            throw new EndOfStreamException("The source response ended before completion.");
        }
        if (ParserState == LLMParserStateEnum.Pre) {
            throw new NotSupportedException("Responses without generated text are not supported.");
        }
        _output = null;
        _pendingBytes = Array.Empty<byte>();
    }

    private void WriteContent(NonNullString content) {
        (_output ?? throw new InvalidOperationException("No data output callback has been installed."))(content);
    }

    protected void EndData() {
        if (ParserState == LLMParserStateEnum.Data) {
            ParserState = LLMParserStateEnum.Post;
        }
    }

    protected abstract void ObservePreambleValue(ref Utf8JsonReader reader);

    protected virtual void ObservePostValue(ref Utf8JsonReader reader) {
        bool envelope = Containers.Count == 1 || (Containers.Count == 2
            && Containers[1].Name is "response" or "message");
        if (reader.TokenType == JsonTokenType.String) {
#if LLM_SKIP_UNUSED_STRING_DECODING
            if (PropertyName is not ("finish_reason" or "stop_reason" or "stop_sequence")
                && !(PropertyName == "reason" && Containers[^1].Name == "incomplete_details")
                && !(PropertyName == "status" && envelope)
                && !(PropertyName == "id" && (envelope || Containers[^1].Name is "item" or "output"))
                && !(!CompletesAtRoot && Containers.Count == 1 && PropertyName is "type" or "error")) {
                return;
            }
#endif
            string? value = reader.GetString();
            if (!CompletesAtRoot && ((Containers.Count == 1 && PropertyName == "type"
                && value is "response.failed" or "error")
                || (envelope && PropertyName == "status" && value == "failed")
                || (Containers.Count == 1 && PropertyName == "error" && !string.IsNullOrEmpty(value)))) {
                Postamble.Status = "failed";
                throw new IOException($"The source response stream failed ({value}).");
            }
            if (PropertyName is "finish_reason" or "stop_reason" && !string.IsNullOrEmpty(value)) {
                Postamble.FinishReason = value switch {
                    "end_turn" or "stop_sequence" => "stop",
                    "max_tokens" or "max_output_tokens" => "length",
                    "tool_use" => "tool_calls",
                    _ => value
                };
            }
            else if (PropertyName == "reason" && Containers[^1].Name == "incomplete_details") {
                Postamble.FinishReason = value == "max_output_tokens" ? "length" : value;
            }
            else if (PropertyName == "status" && envelope) {
                Postamble.Status = value;
            }
            else if (PropertyName == "stop_sequence") {
                Postamble.StopSequence = value;
            }
            else if (PropertyName == "id" && envelope) {
                Postamble.ResponseId = value;
                if (Containers.Count == 2 && Containers[1].Name == "message") {
                    Postamble.MessageId = value;
                }
            }
            else if (PropertyName == "id" && Containers[^1].Name is "item" or "output") {
                Postamble.MessageId = value;
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long value)) {
            if (envelope && PropertyName is "created" or "created_at") {
                Postamble.CreatedAt = value;
            }
            if (Containers.Any(container => container.Name == "usage")) {
                switch (PropertyName) {
                    case "prompt_tokens":
                    case "input_tokens": Postamble.InputTokens = value; break;
                    case "completion_tokens":
                    case "output_tokens": Postamble.OutputTokens = value; break;
                    case "total_tokens": Postamble.TotalTokens = value; break;
                    case "cached_tokens":
                    case "cache_read_input_tokens": Postamble.CachedInputTokens = value; break;
                    case "cache_creation_input_tokens": Postamble.CacheCreationInputTokens = value; break;
                    case "reasoning_tokens": Postamble.ReasoningOutputTokens = value; break;
                }
            }
        }
    }

    protected virtual void ReleasePreambleState() { }

    protected void CaptureEnvelope(ref Utf8JsonReader reader, bool envelope, bool message) {
        if (reader.TokenType == JsonTokenType.String) {
            if (PropertyName == "id" && (envelope || message)) {
                string? identifier = reader.GetString();
                if (envelope) {
                    Preamble.ResponseId = identifier;
                }
                if (message) {
                    Preamble.MessageId = identifier;
                }
            }
            else if (PropertyName == "model" && envelope) {
                Preamble.Model = reader.GetString();
            }
            else if (PropertyName == "role" && message) {
                Preamble.Role = reader.GetString();
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && envelope
            && PropertyName is "created" or "created_at") {
            Preamble.CreatedAt = reader.GetInt64();
        }
    }

    protected abstract bool IsContentValue(ref Utf8JsonReader reader);

    protected virtual void ObserveStringValue(ref Utf8JsonReader reader) { }

    protected virtual void ResetJsonContext() { }

    protected void CompleteJsonEvent() {
        ProcessJson(ReadOnlySpan<char>.Empty, 0, true, ContentWriter);
        _readerState = default;
    }

    protected void ProcessJson(ReadOnlySpan<char> payload, int valueOffset, bool finalBlock = false, Action<NonNullString>? writeContent = null) {
        int previousCount = _pendingCount;
#if LLM_UTF8_TRYGETBYTES
        int requiredCapacity = checked(previousCount + payload.Length + (finalBlock ? 0 : 1));
        if (_pendingBytes.Length < requiredCapacity) {
            Array.Resize(ref _pendingBytes, Math.Max(requiredCapacity, _pendingBytes.Length * 2));
        }
        if (!Encoding.UTF8.TryGetBytes(payload,
            _pendingBytes.AsSpan(previousCount, _pendingBytes.Length - previousCount - (finalBlock ? 0 : 1)), out int bytesWritten)) {
            requiredCapacity = checked(previousCount + Encoding.UTF8.GetByteCount(payload) + (finalBlock ? 0 : 1));
            Array.Resize(ref _pendingBytes, Math.Max(requiredCapacity, _pendingBytes.Length * 2));
            bytesWritten = Encoding.UTF8.GetBytes(payload, _pendingBytes.AsSpan(previousCount));
        }
        _pendingCount += bytesWritten;
    #else
        int requiredCapacity = checked(previousCount + Encoding.UTF8.GetByteCount(payload) + (finalBlock ? 0 : 1));
        if (_pendingBytes.Length < requiredCapacity) {
            Array.Resize(ref _pendingBytes, Math.Max(requiredCapacity, _pendingBytes.Length * 2));
        }
        _pendingCount += Encoding.UTF8.GetBytes(payload, _pendingBytes.AsSpan(_pendingCount));
    #endif
        if (!finalBlock) {
            _pendingBytes[_pendingCount++] = (byte)'\n';
        }

        var reader = new Utf8JsonReader(_pendingBytes.AsSpan(0, _pendingCount), finalBlock, _readerState);
        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.PropertyName) {
                PropertyName = reader.GetString()!;
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) {
                if (!CompletesAtRoot && PropertyName == "error"
                    && (Containers.Count == 1 || (Containers.Count == 2 && Containers[1].Name == "response"))) {
                    Postamble.Status = "failed";
                    throw new IOException("The source response stream returned an error payload.");
                }
                if (Containers.Count == 1 && reader.TokenType == JsonTokenType.StartArray) {
                    CollectionItemIndex = -1;
                }
                else if (Containers.Count == 2 && Containers[^1].IsArray && reader.TokenType == JsonTokenType.StartObject) {
                    CollectionItemIndex++;
                }
                Containers.Add((PropertyName, reader.TokenType == JsonTokenType.StartArray));
                PropertyName = string.Empty;
            }
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) {
                bool contentEnded = ParserState == LLMParserStateEnum.Data && IsContentCollectionEnd;
                Containers.RemoveAt(Containers.Count - 1);
                if (contentEnded) {
                    ParserState = LLMParserStateEnum.Post;
                }
                if (Containers.Count == 0) {
                    if (CompletesAtRoot) {
                        IsComplete = true;
                    }
                    ResetJsonContext();
                    _candidateLineIndex = -1;
                    _candidateOffset = -1;
                    _candidateContent = null;
                }
                PropertyName = string.Empty;
            }
            else {
                ObservePostValue(ref reader);
                if (ParserState == LLMParserStateEnum.Pre) {
                    ObservePreambleValue(ref reader);
                }
                if (reader.TokenType == JsonTokenType.String) {
                    ObserveStringValue(ref reader);
                    if (ParserState == LLMParserStateEnum.Data && writeContent is not null) {
                        if (IsContentValue(ref reader) && IsContentContext) {
                            writeContent(new NonNullString(reader.GetString()!));
                        }
                    }
                    else if (ParserState == LLMParserStateEnum.Pre && reader.ValueSpan.Length > 0) {
                        if (_candidateLineIndex < 0 && IsContentValue(ref reader)) {
                            _candidateLineIndex = _lineIndex;
                            _candidateContent = reader.GetString();
                            int tokenOffset = checked((int)reader.TokenStartIndex);
                            _candidateOffset = valueOffset + Encoding.UTF8.GetCharCount(
                                _pendingBytes.AsSpan(previousCount, tokenOffset - previousCount + 1));
                        }
                        if (_candidateLineIndex >= 0 && IsContentContext) {
                            ContentLineIndex = _candidateLineIndex;
                            ContentStartOffset = _candidateOffset;
                            ParserState = LLMParserStateEnum.Data;
                            PropertyName = string.Empty;
                            break;
                        }
                    }
                }
                PropertyName = string.Empty;
            }
        }

        int consumed = checked((int)reader.BytesConsumed);
        _readerState = reader.CurrentState;
        _pendingCount -= consumed;
        _pendingBytes.AsSpan(consumed, _pendingCount).CopyTo(_pendingBytes);
    }
}