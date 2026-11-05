using System.Buffers;
using System.Text;
using System.Text.Json;
using SimpleL7Proxy.Tokenomics;
using System.Collections.Frozen;

namespace SimpleL7Proxy.Tokenomics.Llm;
public class ModelSwapper
{
    private readonly ILogger<ModelSwapper> _logger;
    private readonly TokenomicsHandler _tokenomicsHandler;

    public ModelSwapper(ILogger<ModelSwapper> logger, TokenomicsHandler tokenomicsHandler)
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
    /// Single parse: extracts model, word count, and captures all top-level field offsets.
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
            using var doc = JsonDocument.Parse(bodyBytes);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return result;

            int wordCount = 0;
            foreach (var prop in root.EnumerateObject())
            {
                // Capture field offset
                var fieldStart = bodyBytes.Span.IndexOf(Encoding.UTF8.GetBytes($"\"{prop.Name}\""));
                var rawValue = prop.Value.GetRawText();
                var fieldEnd = bodyBytes.Span[(fieldStart + prop.Name.Length + 2)..].IndexOf(Encoding.UTF8.GetBytes("}")) + fieldStart + prop.Name.Length + 2;

                if (fieldStart >= 0)
                {
                    result.TopLevelFields.Add((prop.Name, fieldStart, fieldEnd));
                }

                // Track model value offset specially
                if (prop.Name == "model" && prop.Value.ValueKind == JsonValueKind.String)
                {
                    result.SourceModel = prop.Value.GetString();
                    var modelLocation = prop.Value.GetRawText();
                    result.ModelValueOffset = FindOffset(bodyBytes.Span, modelLocation);
                }

                // Count words
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    wordCount += CountWords(prop.Value.GetString() ?? "");
                }
            }
            result.WordCount = wordCount;
        }
        catch (JsonException)
        {
            // Leave result in partially-parsed state
        }

        return result;
    }

    /// <summary>
    /// Merges using pre-captured offsets and field transforms.
    /// Single pass: copy original, skip removed fields, replace model value.
    /// </summary>
    public static ReadOnlyMemory<byte> MergeModel(
        string? sourceModel,
        string newModel,
        ModelParseResult parseResult)
    {
        var (fieldsToRemove, fieldsToRename) = !string.IsNullOrWhiteSpace(parseResult.SourceModel)
            ? ModelMap.Get(parseResult.SourceModel, newModel)
            : (FrozenSet<string>.Empty, FrozenDictionary<string, string>.Empty);

        if (sourceModel == newModel && fieldsToRemove.Count == 0 && fieldsToRename.Count == 0)
            return parseResult.OriginalBodyBytes;

        var buffer = new ArrayBufferWriter<byte>(parseResult.OriginalBodyBytes.Length + newModel.Length + 16);
        var originalSpan = parseResult.OriginalBodyBytes.Span;
        int pos = 0;
        var newModelBytes = Encoding.UTF8.GetBytes($"\"{newModel}\"");

        while (pos < originalSpan.Length)
        {
            // Replace model value
            if (parseResult.ModelValueOffset.HasValue && pos == parseResult.ModelValueOffset.Value.Start)
            {
                buffer.Write(newModelBytes);
                pos = parseResult.ModelValueOffset.Value.End;
                continue;
            }

            // Skip fields marked for removal
            var fieldToRemove = parseResult.TopLevelFields.FirstOrDefault(f => pos >= f.Start && pos < f.End && fieldsToRemove.Contains(f.Name));
            if (fieldToRemove.Name != null)
            {
                pos = fieldToRemove.End;
                continue;
            }

            var span = buffer.GetSpan(1);
            span[0] = originalSpan[pos];
            buffer.Advance(1);
            pos++;
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
        // 1. Parse once: model, word count, all field offsets
        var parseResult = ParseModel(bodyBytes);
        request.WordCount = parseResult.WordCount;

        // 2. Decide final model
        string finalModel = modelOverride switch
        {
            ModelOverrideEnum.None => parseResult.SourceModel ?? "unknown",
            ModelOverrideEnum.Upgrade or ModelOverrideEnum.Downgrade
                => _tokenomicsHandler.UpdateModel(parseResult.SourceModel ?? "unknown", modelOverride),
            _ => modelOverrideName
        };

        request.Model = finalModel;

        // 3. Get transforms and merge

        return MergeModel(parseResult.SourceModel, finalModel, parseResult);
    }
}