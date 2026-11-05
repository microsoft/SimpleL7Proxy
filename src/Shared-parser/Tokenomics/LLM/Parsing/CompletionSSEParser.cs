using System.Text.Json;

namespace SharedParser.Tokenomics.LLM.Parsers;

/// <summary>Extracts Chat Completions text deltas and captures response metadata.</summary>
public sealed class CompletionSSEParser : LLMSSEApiParser {
    protected override bool CompletesAtDone => true;
    protected override void ObservePreambleValue(ref Utf8JsonReader reader) {
        bool envelope = Containers is [(_, false)];
        bool message = Containers is [_, ("choices", true), _, ("delta", false)]
            && CollectionItemIndex == 0;
        CaptureEnvelope(ref reader, envelope, message);
    }

    protected override bool IsContentValue(ref Utf8JsonReader reader) =>
        PropertyName == "content"
        && Containers is [(_, false), ("choices", true), (_, false), ("delta", false)];
}