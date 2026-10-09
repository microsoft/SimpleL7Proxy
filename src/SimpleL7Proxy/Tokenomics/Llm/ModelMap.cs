using System.Collections.Frozen;
using System.Collections.Immutable;

using SimpleL7Proxy.Tokenomics.Llm;

public static class ModelMap
{
    /// <summary>
    /// Gets the field removal and rename maps for a model transition.
    /// </summary>
    public static (FrozenSet<string> FieldsToRemove, FrozenDictionary<string, string> FieldsToRename) Get(
        string sourceModel,
        string destinationModel)
    {
        var sourceFamily = GetFamily(sourceModel);
        var destinationFamily = GetFamily(destinationModel);

        return (sourceFamily, destinationFamily) switch
        {
            (ModelFamily.Classic, ModelFamily.Gpt5) =>
                (FieldRemovalMap.ClassicToReasoning, FieldRenameMap.ClassicToReasoning),
            (ModelFamily.Classic, ModelFamily.Reasoning) =>
                (FieldRemovalMap.ClassicToReasoning, FieldRenameMap.ClassicToReasoning),
            (ModelFamily.Gpt5, ModelFamily.Classic) =>
                (FieldRemovalMap.Gpt5ToClassic, FieldRenameMap.ReasoningToClassic),
            (ModelFamily.Gpt5, ModelFamily.Reasoning) =>
                (FieldRemovalMap.Gpt5ToReasoning, FieldRenameMap.Empty),
            (ModelFamily.Reasoning, ModelFamily.Classic) =>
                (FieldRemovalMap.ReasoningToClassic, FieldRenameMap.ReasoningToClassic),
            _ => (FieldRemovalMap.Empty, FieldRenameMap.Empty)
        };
    }

    private static ModelFamily GetFamily(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return ModelFamily.Unknown;
        }

        ReadOnlySpan<char> modelName = model.AsSpan().Trim();

        if (IsModel(modelName, "gpt-5"))
        {
            return ModelFamily.Gpt5;
        }

        if (IsModel(modelName, "o3") || IsModel(modelName, "o4-mini"))
        {
            return ModelFamily.Reasoning;
        }

        if (IsModel(modelName, "gpt-4")
            || IsModel(modelName, "gpt-4o")
            || IsModel(modelName, "gpt-4.1"))
        {
            return ModelFamily.Classic;
        }

        return ModelFamily.Unknown;
    }

    private static bool IsModel(ReadOnlySpan<char> model, ReadOnlySpan<char> canonicalName)
    {
        return model.Equals(canonicalName, StringComparison.OrdinalIgnoreCase)
            || (model.Length > canonicalName.Length
                && model[canonicalName.Length] == '-'
                && model.StartsWith(canonicalName, StringComparison.OrdinalIgnoreCase));
    }



    public readonly record struct UsageFieldMap(
    ImmutableArray<string> PromptTokens,
    ImmutableArray<string> CachedTokens,
    ImmutableArray<string> CompletionTokens,
    ImmutableArray<string> TotalTokens,
    ImmutableArray<string> ReasoningTokens,
    ImmutableArray<string> CacheCreationTokens,
    bool AddCacheTokensToPrompt,
    bool AddReasoningTokensToCompletion);

    private static readonly FrozenDictionary<string, UsageProvider> UsageProviderByModel =
        new Dictionary<string, UsageProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-"] = UsageProvider.OpenAI,
            ["chatgpt-"] = UsageProvider.OpenAI,
            ["o1"] = UsageProvider.OpenAI,
            ["o3"] = UsageProvider.OpenAI,
            ["o4"] = UsageProvider.OpenAI,
            ["mistral-"] = UsageProvider.OpenAI,
            ["ministral-"] = UsageProvider.OpenAI,
            ["codestral-"] = UsageProvider.OpenAI,
            ["deepseek-"] = UsageProvider.OpenAI,
            ["grok-"] = UsageProvider.OpenAI,

            ["claude-"] = UsageProvider.Anthropic,

            ["gemini-"] = UsageProvider.Gemini,
            ["models/gemini-"] = UsageProvider.Gemini,

            ["command-"] = UsageProvider.Cohere,
            ["c4ai-"] = UsageProvider.Cohere,

            ["amazon.nova"] = UsageProvider.Bedrock,
            ["anthropic.claude"] = UsageProvider.Bedrock,
            ["us.anthropic.claude"] = UsageProvider.Bedrock,
            ["eu.anthropic.claude"] = UsageProvider.Bedrock,
            ["apac.anthropic.claude"] = UsageProvider.Bedrock,
            ["cohere.command"] = UsageProvider.Bedrock,
            ["meta.llama3"] = UsageProvider.Bedrock,
            ["meta.llama4"] = UsageProvider.Bedrock,
            ["mistral.mistral"] = UsageProvider.Bedrock
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<UsageProvider, UsageFieldMap> UsageFields =
        new Dictionary<UsageProvider, UsageFieldMap>
        {
            [UsageProvider.OpenAI] = new(
                ["usage.prompt_tokens"],
                ["usage.prompt_tokens_details.cached_tokens"],
                ["usage.completion_tokens"],
                ["usage.total_tokens"],
                ["usage.completion_tokens_details.reasoning_tokens"],
                [],
                false,
                false),

            [UsageProvider.Anthropic] = new(
                ["message.usage.input_tokens", "usage.input_tokens"],
                ["message.usage.cache_read_input_tokens", "usage.cache_read_input_tokens"],
                ["message.usage.output_tokens", "usage.output_tokens"],
                [],
                [],
                ["message.usage.cache_creation_input_tokens", "usage.cache_creation_input_tokens"],
                true,
                false),

            [UsageProvider.Gemini] = new(
                ["usageMetadata.promptTokenCount"],
                ["usageMetadata.cachedContentTokenCount"],
                ["usageMetadata.candidatesTokenCount"],
                ["usageMetadata.totalTokenCount"],
                ["usageMetadata.thoughtsTokenCount"],
                [],
                false,
                true),

            [UsageProvider.Bedrock] = new(
                [
                    "usage.inputTokens",
                "metadata.usage.inputTokens",
                "amazon-bedrock-invocationMetrics.inputTokenCount"
                ],
                [
                    "usage.cacheReadInputTokens",
                "metadata.usage.cacheReadInputTokens"
                ],
                [
                    "usage.outputTokens",
                "metadata.usage.outputTokens",
                "amazon-bedrock-invocationMetrics.outputTokenCount"
                ],
                [
                    "usage.totalTokens",
                "metadata.usage.totalTokens"
                ],
                [],
                [
                    "usage.cacheWriteInputTokens",
                "metadata.usage.cacheWriteInputTokens"
                ],
                false,
                false),

            [UsageProvider.Cohere] = new(
                [
                    "usage.billed_units.input_tokens",
                "meta.billed_units.input_tokens",
                "delta.usage.billed_units.input_tokens"
                ],
                [],
                [
                    "usage.billed_units.output_tokens",
                "meta.billed_units.output_tokens",
                "delta.usage.billed_units.output_tokens"
                ],
                [],
                [],
                [],
                false,
                false)
        }.ToFrozenDictionary();

    public static UsageProvider GetUsageProvider(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return UsageProvider.Unknown;
        }

        ReadOnlySpan<char> modelName = model.AsSpan().Trim();
        var provider = UsageProvider.Unknown;
        var matchedLength = 0;

        foreach (var entry in UsageProviderByModel)
        {
            if (entry.Key.Length > matchedLength &&
                modelName.StartsWith(entry.Key, StringComparison.OrdinalIgnoreCase))
            {
                provider = entry.Value;
                matchedLength = entry.Key.Length;
            }
        }

        return provider;
    }

    public static bool TryGetUsageFields(
        UsageProvider provider,
        out UsageFieldMap fields)
    {
        return UsageFields.TryGetValue(provider, out fields);
    }
}