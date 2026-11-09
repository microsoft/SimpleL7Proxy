using System.Collections.Frozen;

namespace SharedParser.Tokenomics.LLM.Models;

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

    public static FrozenSet<string> ClassicToGpt6 { get; } = new[]
    {
        "temperature"
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

    public static FrozenSet<string> Gpt6ToReasoning { get; } =
        ClassicToReasoning.Concat(Gpt5ToReasoning).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> ToAnthropic { get; } = new[]
    {
        "frequency_penalty",
        "presence_penalty",
        "logit_bias",
        "logprobs",
        "top_logprobs",
        "n",
        "seed",
        "reasoning_effort",
        "verbosity",
        "response_format",
        "parallel_tool_calls",
        "stream_options"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> AnthropicToClassic { get; } = new[]
    {
        "top_k",
        "thinking",
        "output_config"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static FrozenSet<string> AnthropicToReasoning { get; } =
        AnthropicToClassic.Concat(ClassicToReasoning).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}