using System.Diagnostics.CodeAnalysis;
using SharedParser.Tokenomics.LLM.Catalog;

namespace SharedParser.Tokenomics.LLM.Models;

/// <summary>A model name and API format validated once at construction, without a provider lookup.</summary>
public sealed class ValidModel {
    /// <summary>Gets the non-empty, non-whitespace model name.</summary>
    public NonNullString Name { get; }

    /// <summary>Gets the validated API format.</summary>
    public LLMApiEnum ApiFormat { get; }

    /// <summary>Creates a model with a non-empty name and a defined API format.</summary>
    public ValidModel(string name, LLMApiEnum apiFormat) {
        var modelName = new NonNullString(name);
        if (modelName.Value.AsSpan().IsWhiteSpace()) {
            throw new ArgumentException("The model name must not be empty or whitespace.", nameof(name));
        }
        if (apiFormat is not (LLMApiEnum.OpenAIChatCompletions or LLMApiEnum.OpenAIResponses or LLMApiEnum.AnthropicMessages)) {
            throw new ArgumentOutOfRangeException(nameof(apiFormat), apiFormat, "Unsupported model API format.");
        }

        Name = modelName;
        ApiFormat = apiFormat;
    }

    private ValidModel(NonNullString name, LLMApiEnum apiFormat) {
        Name = name;
        ApiFormat = apiFormat;
    }

    /// <summary>Parses model.APIFormat without throwing for invalid input.</summary>
    public static bool TryParse(NonNullString value, [NotNullWhen(true)] out ValidModel? model) {
        model = null;
        var input = value.Value.AsSpan();
        int separator = input.LastIndexOf('.');
        if (separator <= 0 || separator == input.Length - 1) {
            return false;
        }

        var modelName = input[..separator].Trim();
        var formatName = input[(separator + 1)..].Trim();
        if (modelName.IsEmpty) {
            return false;
        }

        LLMApiEnum apiFormat;
        if (formatName.Equals("Completions", StringComparison.OrdinalIgnoreCase)
            || formatName.Equals("OpenAIChatCompletions", StringComparison.OrdinalIgnoreCase)) {
            apiFormat = LLMApiEnum.OpenAIChatCompletions;
        }
        else if (formatName.Equals("Responses", StringComparison.OrdinalIgnoreCase)
            || formatName.Equals("OpenAIResponses", StringComparison.OrdinalIgnoreCase)) {
            apiFormat = LLMApiEnum.OpenAIResponses;
        }
        else if (formatName.Equals("Messages", StringComparison.OrdinalIgnoreCase)
            || formatName.Equals("AnthropicMessages", StringComparison.OrdinalIgnoreCase)) {
            apiFormat = LLMApiEnum.AnthropicMessages;
        }
        else {
            return false;
        }

        model = new ValidModel(new NonNullString(modelName.ToString()), apiFormat);
        return true;
    }
}