using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsing;

/// <summary>Parses Anthropic Messages JSON responses.</summary>
public sealed class AnthropicMessagesParser : LLMApiParser {
    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        bool message = Containers.Count == 1 && !Containers[0].IsArray;
        CaptureEnvelope(ref reader, message, message);
    }

    protected override bool IsContentValue(ref Utf8JsonReader reader) =>
        PropertyName == "text" && Containers.Count == 3 && !Containers[0].IsArray
        && Containers[1] == ("content", true) && !Containers[2].IsArray;
}