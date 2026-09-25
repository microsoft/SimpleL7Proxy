using System.Collections.Frozen;

namespace SimpleL7Proxy.Llm;

public static class FieldRemovalMap
{
    public static FrozenSet<string> Empty { get; } =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> ClassicToReasoning { get; } = new[]
    {
        "frequency_penalty",
        "presence_penalty",
        "stop",
        "temperature",
        "top_p"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> Gpt5ToClassic { get; } = new[]
    {
        "reasoning_effort",
        "verbosity"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> ReasoningToClassic { get; } = new[]
    {
        "reasoning_effort"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> Gpt5ToReasoning { get; } = new[]
    {
        "verbosity"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}