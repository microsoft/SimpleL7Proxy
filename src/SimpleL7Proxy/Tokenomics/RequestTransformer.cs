using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Collections.Frozen;
using SharedParser.Tokenomics.LLM.Models;
using SharedParser.Tokenomics.LLM.Catalog;

namespace SimpleL7Proxy.Tokenomics;
public class RequestTransformer {
    private readonly ILogger<RequestTransformer> _logger;
    private readonly TokenomicsHandler _tokenomicsHandler;

    public RequestTransformer(ILogger<RequestTransformer> logger, TokenomicsHandler tokenomicsHandler)
    {
        _logger = logger;
        _tokenomicsHandler = tokenomicsHandler;
    }

    public class ModelParseResult
    {
        public string? SourceModel { get; set; }
        public int WordCount { get; set; }
        public ReadOnlyMemory<byte> OriginalBodyBytes { get; set; }

        // All captured in one parse
        public (int Start, int End)? ModelValueOffset { get; set; }
        public List<(string Name, int Start, int End)> TopLevelFields { get; set; } = new();
    }

    /// <summary>
    /// Detects the API format from the request path, returning Unknown for unrecognized paths.
    /// </summary>
    public static LLMApiEnum ModelApiName(string path) =>
        LLMApiEnumParser.TryParsePath(path, out var api) ? api : LLMApiEnum.Unknown;

    /// <summary>
    /// Extracts the model and word count without collecting rewrite metadata.
    /// </summary>
    public static ModelParseResult ParseModel(ReadOnlyMemory<byte> bodyBytes)
    {
        var result = new ModelParseResult
        {
            OriginalBodyBytes = bodyBytes,
            WordCount = 0,
            SourceModel = null
        };

        try
        {
            var reader = new Utf8JsonReader(bodyBytes.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return result;

            int wordCount = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                bool isModel = reader.ValueTextEquals("model"u8);
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString() ?? "";
                    if (isModel)
                        result.SourceModel = value;
                    wordCount += CountWords(value);
                }
                reader.Skip();
            }
            reader.Read();
            result.WordCount = wordCount;
        }
        catch (JsonException)
        {
            result.SourceModel = null;
        }

        return result;
    }

    /// <summary>
    /// Returns unchanged bodies directly; rewrites top-level fields using JSON token boundaries.
    /// </summary>
    public static ReadOnlyMemory<byte> MergeModel(
        string? sourceModel,
        string newModel,
        ModelParseResult parseResult)
    {
        if (sourceModel == newModel && parseResult.SourceModel == newModel)
            return parseResult.OriginalBodyBytes;

        if (string.IsNullOrWhiteSpace(parseResult.SourceModel))
            return parseResult.OriginalBodyBytes;

        var (fieldsToRemove, fieldsToRename) = !string.IsNullOrWhiteSpace(parseResult.SourceModel)
            ? ModelMap.Get(parseResult.SourceModel, newModel)
            : (FrozenSet<string>.Empty, FrozenDictionary<string, string>.Empty);

        var buffer = new ArrayBufferWriter<byte>(parseResult.OriginalBodyBytes.Length + newModel.Length + 16);
        var originalSpan = parseResult.OriginalBodyBytes.Span;
        var reader = new Utf8JsonReader(originalSpan);
        reader.Read();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString()!;
                reader.Read();
                int valueStart = checked((int)reader.TokenStartIndex);
                reader.Skip();
                int valueEnd = checked((int)reader.BytesConsumed);

                if (name == "model")
                {
                    writer.WriteString(name, newModel);
                    continue;
                }

                if (fieldsToRemove.Contains(name))
                    continue;

                writer.WritePropertyName(fieldsToRename.TryGetValue(name, out var renamed) ? renamed : name);
                writer.WriteRawValue(originalSpan[valueStart..valueEnd], skipInputValidation: true);
            }
            reader.Read();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    /// <summary>
    /// Finds the byte offset of a substring in the original body.
    /// </summary>
    private static (int Start, int End)? FindOffset(ReadOnlySpan<byte> data, string target)
    {
        var targetBytes = Encoding.UTF8.GetBytes(target);
        int index = data.IndexOf(targetBytes);
        return index >= 0 ? (index, index + targetBytes.Length) : null;
    }

    /// <summary>
    /// Counts whitespace-delimited words in the given text.
    /// </summary>
    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int wordCount = 0;
        bool inWord = false;

        foreach (char c in text)
        {
            bool isWhiteSpace = char.IsWhiteSpace(c);

            if (isWhiteSpace)
            {
                inWord = false;
            }
            else if (!inWord)
            {
                wordCount++;
                inWord = true;
            }
        }

        return wordCount;
    }

    public ReadOnlyMemory<byte> ValidateModel(
        RequestData request,
        ReadOnlyMemory<byte> bodyBytes,
        ModelOverrideEnum modelOverride,
        string modelOverrideName)
    {
        // 1. Parse once: model and word count
        var parseResult = ParseModel(bodyBytes);
        request.WordCount = parseResult.WordCount;

        // 2. Decide final model
        var (finalModel, finalAPIName) = modelOverride switch
        {
            ModelOverrideEnum.None => (parseResult.SourceModel ?? "unknown", request.EffectiveAPIName),
            ModelOverrideEnum.Upgrade or ModelOverrideEnum.Downgrade
                => _tokenomicsHandler.UpdateModelDestination(
                    parseResult.SourceModel ?? "unknown", request.EffectiveAPIName, modelOverride),
            _ => (modelOverrideName, request.EffectiveAPIName)
        };

        request.EffectiveModel = finalModel;
        request.EffectiveAPIName = finalAPIName;

        // 3. Get transforms and merge

        return MergeModel(parseResult.SourceModel, finalModel, parseResult);
    }
}