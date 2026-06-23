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

    private static bool LooksLikeJson(string payload)
    {
        return payload.StartsWith("{", StringComparison.Ordinal) || payload.StartsWith("[", StringComparison.Ordinal);
    }
}
