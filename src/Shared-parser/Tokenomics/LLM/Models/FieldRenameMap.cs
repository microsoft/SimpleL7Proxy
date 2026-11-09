using System.Collections.Frozen;

namespace SharedParser.Tokenomics.LLM.Models;

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

    public static FrozenDictionary<string, string> ClassicToAnthropic { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_completion_tokens"] = "max_tokens",
            ["stop"] = "stop_sequences"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, string> AnthropicToClassic { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["stop_sequences"] = "stop"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, string> AnthropicToReasoning { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_tokens"] = "max_completion_tokens"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static FrozenDictionary<string, string> AnthropicToGpt6 { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max_tokens"] = "max_completion_tokens",
            ["stop_sequences"] = "stop"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}