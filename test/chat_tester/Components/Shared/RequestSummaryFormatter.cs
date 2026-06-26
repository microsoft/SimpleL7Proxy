using System.Text.Json;
using System.Text.RegularExpressions;

namespace chat_tester.Components.Shared;

public static class RequestSummaryFormatter
{
    public static string BuildRequestTitle(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No request body";
        }

        if (TryExtractApiMessage(value, out var apiMessage))
        {
            return PreviewText(apiMessage, 260);
        }

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None)
            .Take(4);

        return PreviewText(string.Join("\\n", lines), 260);
    }

    public static string BuildTargetHost(ChatHistoryEntry entry)
    {
        var backendUrl = ExtractBackendUrl(entry.ResponseHeadersText);
        if (!string.IsNullOrWhiteSpace(backendUrl))
        {
            return FormatHost(backendUrl);
        }

        return FormatHost(entry.ServerBaseUrl);
    }

    public static string PreviewText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }

    private static string ExtractBackendUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = Regex.Match(
            value,
            @"\bUsing\s+[^|\r\n]*?URL:\s*(?<url>https?://[^\s|]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return match.Success ? TrimUrl(match.Groups["url"].Value) : string.Empty;
    }

    private static string FormatHost(string value)
    {
        var trimmed = TrimUrl(value);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "-";
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : trimmed;
    }

    private static string TrimUrl(string value) =>
        value.Trim().TrimEnd('.', ',', ';', ')', ']');

    private static bool TryExtractApiMessage(string value, out string message)
    {
        message = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            if (TryExtractLastMessageFromProperty(root, "messages", out message) ||
                TryExtractLastMessageFromProperty(root, "input", out message) ||
                TryExtractLastMessageFromProperty(root, "contents", out message))
            {
                return true;
            }

            foreach (var propertyName in new[] { "message", "prompt", "query", "question", "text", "input" })
            {
                if (TryGetPropertyIgnoreCase(root, propertyName, out var property) && TryExtractText(property, out message))
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryExtractLastMessageFromProperty(JsonElement root, string propertyName, out string message)
    {
        message = string.Empty;
        return TryGetPropertyIgnoreCase(root, propertyName, out var property) &&
            TryExtractLastMessage(property, out message);
    }

    private static bool TryExtractLastMessage(JsonElement value, out string message)
    {
        message = string.Empty;

        if (value.ValueKind == JsonValueKind.String)
        {
            message = CompactWhitespace(value.GetString() ?? string.Empty);
            return !string.IsNullOrWhiteSpace(message);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return TryExtractMessageText(value, out message);
        }

        var lastMessage = string.Empty;
        var lastUserMessage = string.Empty;
        foreach (var item in value.EnumerateArray())
        {
            if (!TryExtractMessageText(item, out var itemMessage))
            {
                continue;
            }

            lastMessage = itemMessage;
            if (TryGetPropertyIgnoreCase(item, "role", out var role) &&
                role.ValueKind == JsonValueKind.String &&
                IsUserRole(role.GetString()))
            {
                lastUserMessage = itemMessage;
            }
        }

        message = string.IsNullOrWhiteSpace(lastUserMessage) ? lastMessage : lastUserMessage;
        return !string.IsNullOrWhiteSpace(message);
    }

    private static bool TryExtractMessageText(JsonElement value, out string message)
    {
        message = string.Empty;

        if (value.ValueKind == JsonValueKind.String)
        {
            message = CompactWhitespace(value.GetString() ?? string.Empty);
            return !string.IsNullOrWhiteSpace(message);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return TryExtractText(value, out message);
        }

        foreach (var propertyName in new[] { "content", "parts", "text", "message", "input", "prompt" })
        {
            if (TryGetPropertyIgnoreCase(value, propertyName, out var property) && TryExtractText(property, out message))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractText(JsonElement value, out string text)
    {
        text = string.Empty;

        if (value.ValueKind == JsonValueKind.String)
        {
            text = CompactWhitespace(value.GetString() ?? string.Empty);
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "text", "content", "parts", "message", "input", "prompt" })
            {
                if (TryGetPropertyIgnoreCase(value, propertyName, out var property) && TryExtractText(property, out text))
                {
                    return true;
                }
            }

            return false;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (TryExtractText(item, out var itemText))
            {
                parts.Add(itemText);
            }
        }

        text = CompactWhitespace(string.Join(" ", parts));
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement value, string propertyName, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static bool IsUserRole(string? role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "human", StringComparison.OrdinalIgnoreCase);

    private static string CompactWhitespace(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
