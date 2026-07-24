using System.Text;
using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>
/// Reads values back out of an existing request body so they can be carried over when the
/// user switches models or toggles between simple and advanced modes.
/// </summary>
public static class ModelRequestParser
{
    /// <summary>Returns the template key whose model id matches the body's <c>model</c> field, or empty.</summary>
    public static string DetectModelKey(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("model", out var modelValue) &&
                modelValue.ValueKind == JsonValueKind.String)
            {
                return ModelCatalog.FindByModelId(modelValue.GetString())?.Key ?? string.Empty;
            }
        }
        catch
        {
            // Body is not valid JSON; leave the dropdown unselected.
        }

        return string.Empty;
    }

    /// <summary>Extracts the user message from the body across the supported request shapes.</summary>
    public static string ExtractUserMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                string lastContent = string.Empty;
                foreach (var msg in messages.EnumerateArray())
                {
                    if (msg.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var role = msg.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
                    if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (msg.TryGetProperty("content", out var contentValue))
                    {
                        lastContent = ReadContentValue(contentValue);
                    }
                }

                if (!string.IsNullOrWhiteSpace(lastContent))
                {
                    return lastContent;
                }
            }

            if (root.TryGetProperty("message", out var singleMessage) &&
                singleMessage.ValueKind == JsonValueKind.String)
            {
                return singleMessage.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("contents", out var contents) &&
                contents.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var content in contents.EnumerateArray())
                {
                    if (content.ValueKind == JsonValueKind.Object &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object &&
                                part.TryGetProperty("text", out var text) &&
                                text.ValueKind == JsonValueKind.String)
                            {
                                builder.Append(text.GetString());
                            }
                        }
                    }
                }

                return builder.ToString();
            }

            if (root.TryGetProperty("prompt", out var prompt) &&
                prompt.ValueKind == JsonValueKind.String)
            {
                return prompt.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("input", out var input) &&
                input.ValueKind == JsonValueKind.String)
            {
                return input.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Body is not valid JSON; nothing to carry over.
        }

        return string.Empty;
    }

    private static string ReadContentValue(JsonElement contentValue)
    {
        if (contentValue.ValueKind == JsonValueKind.String)
        {
            return contentValue.GetString() ?? string.Empty;
        }

        if (contentValue.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in contentValue.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    builder.Append(text.GetString());
                }
            }

            return builder.ToString();
        }

        return string.Empty;
    }
}
