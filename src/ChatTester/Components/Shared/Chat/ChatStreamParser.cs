using System.Text;
using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>
/// Parses server-sent event (SSE) chat streams, extracting the assistant text
/// from OpenAI-style <c>delta.content</c> and <c>choices[].delta.content</c> payloads.
/// </summary>
public static class ChatStreamParser
{
    /// <summary>
    /// Extracts the assistant text from a single SSE <c>data:</c> payload.
    /// Plain-text payloads are returned as-is (with surrounding quotes trimmed).
    /// </summary>
    public static string ExtractDeltaText(string dataPayload)
    {
        if (string.IsNullOrWhiteSpace(dataPayload))
        {
            return string.Empty;
        }

        if (!LooksLikeJson(dataPayload))
        {
            return dataPayload.Trim('"');
        }

        try
        {
            using var document = JsonDocument.Parse(dataPayload);
            var builder = new StringBuilder();
            AppendContent(document.RootElement, builder);
            return builder.ToString();
        }
        catch
        {
            // Ignore malformed stream payloads.
            return string.Empty;
        }
    }

    /// <summary>
    /// Extracts and concatenates the assistant text from an entire SSE buffer
    /// (multiple <c>data:</c> lines, optionally terminated by <c>[DONE]</c>).
    /// </summary>
    public static string ExtractAllContent(string sseBuffer)
    {
        if (string.IsNullOrWhiteSpace(sseBuffer))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var line in sseBuffer.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var payload = ExtractDataPayload(line);
            if (payload is null)
            {
                continue;
            }

            builder.Append(ExtractDeltaText(payload));
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Extracts displayable assistant/content text from a complete JSON response body.
    /// Supports OpenAI chat/responses-style shapes and falls back to an empty string.
    /// </summary>
    public static string ExtractDisplayContent(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || !LooksLikeJson(responseBody.TrimStart()))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var builder = new StringBuilder();
            AppendContent(document.RootElement, builder);
            return builder.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the payload of an SSE <c>data:</c> line, or <c>null</c> when the
    /// line is not a data line or is the terminating <c>[DONE]</c> marker.
    /// </summary>
    public static string? ExtractDataPayload(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = trimmed["data:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(payload) || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return payload;
    }

    public static ChatTokenUsage ExtractTokenUsage(string responseText)
    {
        var result = new ChatTokenUsage();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return result;
        }

        foreach (var line in responseText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            var payload = ExtractDataPayload(line);
            if (payload is not null && TryExtractTokenUsage(payload, out var streamUsage))
            {
                result.MergeFrom(streamUsage);
            }
        }

        if (result.HasAny)
        {
            return result;
        }

        if (TryExtractTokenUsage(responseText.Trim(), out var bodyUsage))
        {
            result.MergeFrom(bodyUsage);
        }

        return result;
    }

    public static bool TryExtractTokenUsage(string dataPayload, out ChatTokenUsage usage)
    {
        usage = new ChatTokenUsage();
        if (string.IsNullOrWhiteSpace(dataPayload) || !LooksLikeJson(dataPayload.TrimStart()))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(dataPayload);
            if (!TryGetUsageElement(document.RootElement, out var usageElement))
            {
                return false;
            }

            usage = ReadTokenUsage(usageElement);
            return usage.HasAny;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendContent(JsonElement root, StringBuilder builder)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("delta", out var delta) &&
            delta.ValueKind == JsonValueKind.Object &&
            delta.TryGetProperty("content", out var contentValue) &&
            contentValue.ValueKind == JsonValueKind.String)
        {
            builder.Append(contentValue.GetString());
            return;
        }

        if (root.TryGetProperty("type", out var typeValue) &&
            typeValue.ValueKind == JsonValueKind.String &&
            string.Equals(typeValue.GetString(), "response.output_text.delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("delta", out var outputTextDelta) &&
            outputTextDelta.ValueKind == JsonValueKind.String)
        {
            builder.Append(outputTextDelta.GetString());
            return;
        }

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind == JsonValueKind.Object &&
                    choice.TryGetProperty("delta", out var choiceDelta) &&
                    choiceDelta.ValueKind == JsonValueKind.Object &&
                    choiceDelta.TryGetProperty("content", out var choiceContentValue) &&
                    choiceContentValue.ValueKind == JsonValueKind.String)
                {
                    builder.Append(choiceContentValue.GetString());
                }

                if (choice.ValueKind == JsonValueKind.Object &&
                    choice.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out var messageContent) &&
                    messageContent.ValueKind == JsonValueKind.String)
                {
                    builder.Append(messageContent.GetString());
                }
            }
        }

        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            builder.Append(outputText.GetString());
        }

        if (root.TryGetProperty("content", out var rootContent) &&
            rootContent.ValueKind == JsonValueKind.String)
        {
            builder.Append(rootContent.GetString());
        }

        if (root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("content", out var contentArray) ||
                    contentArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in contentArray.EnumerateArray())
                {
                    if (contentItem.ValueKind == JsonValueKind.Object &&
                        contentItem.TryGetProperty("text", out var textValue) &&
                        textValue.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(textValue.GetString());
                    }
                }
            }
        }
    }

    private static bool TryGetUsageElement(JsonElement root, out JsonElement usageElement)
    {
        usageElement = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (root.TryGetProperty("usage", out usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("usage", out usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        return false;
    }

    private static ChatTokenUsage ReadTokenUsage(JsonElement usageElement)
    {
        var usage = new ChatTokenUsage
        {
            InputTokens = ReadInt32Property(usageElement, "input_tokens", "prompt_tokens"),
            OutputTokens = ReadInt32Property(usageElement, "output_tokens", "completion_tokens"),
            TotalTokens = ReadInt32Property(usageElement, "total_tokens"),
            ReasoningTokens = ReadNestedInt32Property(usageElement, "output_tokens_details", "reasoning_tokens")
                ?? ReadNestedInt32Property(usageElement, "completion_tokens_details", "reasoning_tokens"),
            CachedInputTokens = ReadNestedInt32Property(usageElement, "input_tokens_details", "cached_tokens")
                ?? ReadNestedInt32Property(usageElement, "prompt_tokens_details", "cached_tokens")
        };

        if (!usage.TotalTokens.HasValue && usage.InputTokens.HasValue && usage.OutputTokens.HasValue)
        {
            usage.TotalTokens = usage.InputTokens.Value + usage.OutputTokens.Value;
        }

        return usage;
    }

    private static int? ReadNestedInt32Property(JsonElement root, string objectName, string propertyName)
    {
        if (root.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return ReadInt32Property(nested, propertyName);
        }

        return null;
    }

    private static int? ReadInt32Property(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
            {
                return intValue;
            }
        }

        return null;
    }

    private static bool LooksLikeJson(string payload)
    {
        return payload.StartsWith("{", StringComparison.Ordinal) || payload.StartsWith("[", StringComparison.Ordinal);
    }
}

public sealed class ChatTokenUsage
{
    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    public int? CachedInputTokens { get; set; }

    public int? TotalTokens { get; set; }

    public bool HasAny => InputTokens.HasValue
        || OutputTokens.HasValue
        || ReasoningTokens.HasValue
        || CachedInputTokens.HasValue
        || TotalTokens.HasValue;

    public void MergeFrom(ChatTokenUsage usage)
    {
        InputTokens = usage.InputTokens ?? InputTokens;
        OutputTokens = usage.OutputTokens ?? OutputTokens;
        ReasoningTokens = usage.ReasoningTokens ?? ReasoningTokens;
        CachedInputTokens = usage.CachedInputTokens ?? CachedInputTokens;
        TotalTokens = usage.TotalTokens ?? TotalTokens;
    }
}
