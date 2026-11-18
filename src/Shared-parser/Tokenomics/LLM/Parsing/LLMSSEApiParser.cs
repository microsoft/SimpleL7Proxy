namespace SharedParser.Tokenomics.LLM.Parsing;

/// <summary>Handles SSE framing before passing JSON payloads to the token tracker.</summary>
public abstract class LLMSSEApiParser : LLMApiParser {
    private bool _hasEventData;
    protected bool IsTerminalEvent { get; set; }
    protected virtual bool CompletesAtDone => false;

    protected override bool IsContentCollectionEnd => false;
    protected override bool CompletesAtRoot => false;

    protected virtual void ProcessEvent(ReadOnlySpan<char> value) { }

    /// <summary>Finalizes the last SSE payload and rejects missing terminal events.</summary>
    public override void Complete() {
        if (Postamble.Status == "failed") {
            throw new IOException("The source response stream failed.");
        }
        try {
            if (_hasEventData) {
                CompleteJsonEvent();
                _hasEventData = false;
                if (IsTerminalEvent) {
                    IsComplete = true;
                }
            }
        }
        catch (System.Text.Json.JsonException exception) {
            throw new EndOfStreamException("The final SSE payload is incomplete.", exception);
        }
        base.Complete();
    }

    protected override void ProcessPayload(NonNullString line) {
        if (Postamble.Status == "failed") {
            throw new IOException("The source response stream failed.");
        }
        var payload = line.Value.AsSpan();
        if (payload.Length == 0) {
            if (_hasEventData) {
                CompleteJsonEvent();
                _hasEventData = false;
                if (IsTerminalEvent) {
                    IsComplete = true;
                }
            }
            IsTerminalEvent = false;
            ProcessEvent(ReadOnlySpan<char>.Empty);
            return;
        }

        int separator = payload.IndexOf(':');
        if (separator <= 0) {
            return;
        }
        var field = payload[..separator];
        int valueOffset = separator + 1;
        if (valueOffset < payload.Length && payload[valueOffset] == ' ') {
            valueOffset++;
        }
        var value = payload[valueOffset..];
        if (field.SequenceEqual("event")) {
            if (value.SequenceEqual("response.failed") || value.SequenceEqual("error")) {
                Postamble.Status = "failed";
                throw new IOException($"The source response stream failed ({value.ToString()}).");
            }
            ProcessEvent(value);
        }
        else if (field.SequenceEqual("data") && value.SequenceEqual("[DONE]")) {
            if (_hasEventData) {
                CompleteJsonEvent();
                _hasEventData = false;
            }
            EndData();
            if (CompletesAtDone) {
                IsComplete = true;
            }
        }
        else if (field.SequenceEqual("data") && !value.SequenceEqual("[DONE]")
            && (_hasEventData || !value.IsWhiteSpace())) {
            _hasEventData = true;
            ProcessJson(value, valueOffset, writeContent: ContentWriter);
        }
    }
}

