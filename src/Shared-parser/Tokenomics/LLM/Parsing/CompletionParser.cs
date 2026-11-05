using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsers;

/// <summary>Parses Chat Completions JSON responses.</summary>
public sealed class CompletionParser : LLMApiParser {
    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        bool envelope = Containers.Count == 1 && !Containers[0].IsArray;
        bool message = Containers.Count == 4 && Containers[1] == ("choices", true)
            && Containers[3] == ("message", false) && CollectionItemIndex == 0;
        CaptureEnvelope(ref reader, envelope, message);
    }

    protected override bool IsContentValue(ref Utf8JsonReader reader) {
        bool isContent = PropertyName == "content" && Containers.Count == 4 && !Containers[0].IsArray
            && Containers[1] == ("choices", true) && !Containers[2].IsArray
            && Containers[3] == ("message", false);
        if (isContent && CollectionItemIndex > 0) {
            throw new NotSupportedException("Multiple completion choices cannot be mapped to a single assistant message.");
        }
        return isContent;
    }
}