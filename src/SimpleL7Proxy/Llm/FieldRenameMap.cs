using System.Collections.Frozen;

namespace SimpleL7Proxy.Llm;

public static class FieldRenameMap
{
    public static FrozenDictionary<string, string> Empty { get; } =
        new Dictionary<string, string>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, string> ClassicToReasoning { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_tokens"] = "max_completion_tokens"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, string> ReasoningToClassic { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_completion_tokens"] = "max_tokens"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}