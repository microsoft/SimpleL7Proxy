using System.Globalization;
using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>Summary details extracted from a history entry for the hover preview.</summary>
public sealed record HistoryRequestSummary(string Model, string Api, string Stream, string Prompt);

/// <summary>
/// Pure formatting and JSON-extraction helpers used by the request history panel to render
/// dates, status codes, API names, and prompt previews. Stateless and independently testable.
/// </summary>
public static class HistoryEntryFormatter
{
    public static string FormatHistoryDate(DateTimeOffset value) =>
        value.LocalDateTime.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);

    public static string FormatHistoryStatus(ChatHistoryEntry entry)
    {
        var statusCode = GetHistoryStatusCode(entry);
        if (statusCode is not null)
        {
            return statusCode.Value.ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(entry.Metrics.Status) ? "-" : entry.Metrics.Status;
    }

    public static HistoryRequestSummary GetHistoryRequestSummary(ChatHistoryEntry entry)
    {
        var model = ExtractModelFromEndpoint(entry.EndpointPath);
        var stream = InferStreamFromEndpoint(entry.EndpointPath);

        if (!string.IsNullOrWhiteSpace(entry.RequestBody))
        {
            try
            {
                using var document = JsonDocument.Parse(entry.RequestBody);
                var root = document.RootElement;

                if (TryGetProperty(root, "model", out var modelElement))
                {
                    var modelValue = ExtractJsonContent(modelElement);
                    if (!string.IsNullOrWhiteSpace(modelValue))
                    {
                        model = modelValue;
                    }
                }

                if (TryGetProperty(root, "stream", out var streamElement))
                {
                    stream = FormatJsonBoolean(streamElement);
                }
            }
            catch (JsonException)
            {
            }
        }

        return new HistoryRequestSummary(
            string.IsNullOrWhiteSpace(model) ? "-" : model,
            FormatHistoryApi(entry.EndpointPath),
            stream,
            FormatHistoryPromptPreview(entry));
    }

    public static string FormatHistoryPromptPreview(ChatHistoryEntry entry)
    {
        var requestBody = entry.RequestBody;
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return "No prompt captured.";
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (TryExtractPromptPreview(document.RootElement, out var promptPreview))
            {
                return TakePreviewLines(promptPreview);
            }
        }
        catch (JsonException)
        {
        }

        return TakePreviewLines(requestBody);
    }

    public static string FormatHistoryApi(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            return "-";
        }

        var endpoint = endpointPath.Split('?', 2)[0];
        if (endpoint.Contains("chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return "chat/completions";
        }

        if (endpoint.Contains("responses", StringComparison.OrdinalIgnoreCase))
        {
            return "responses";
        }

        if (endpoint.Contains("messages", StringComparison.OrdinalIgnoreCase))
        {
            return "messages";
        }

        if (endpoint.Contains("streamGenerateContent", StringComparison.OrdinalIgnoreCase))
        {
            return "streamGenerateContent";
        }

        if (endpoint.Contains("generateContent", StringComparison.OrdinalIgnoreCase))
        {
            return "generateContent";
        }

        return endpoint;
    }

    public static string InferStreamFromEndpoint(string endpointPath) =>
        endpointPath.Contains("stream", StringComparison.OrdinalIgnoreCase) ? "true" : "-";

    public static string ExtractModelFromEndpoint(string endpointPath)
    {
        if (string.IsNullOrWhiteSpace(endpointPath))
        {
            return string.Empty;
        }

        const string marker = "/models/";
        var markerIndex = endpointPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var start = markerIndex + marker.Length;
        var end = endpointPath.IndexOfAny(new[] { '/', ':', '?' }, start);
        return end < 0 ? endpointPath[start..] : endpointPath[start..end];
    }

    public static string FormatJsonBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value ? "true" : "false",
        _ => "-"
    };

    public static bool TryExtractPromptPreview(JsonElement root, out string promptPreview)
    {
        if (TryGetProperty(root, "messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            var fallbackMessage = string.Empty;
            foreach (var message in messages.EnumerateArray())
            {
                var content = TryGetProperty(message, "content", out var contentElement)
                    ? ExtractJsonContent(contentElement)
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                fallbackMessage = content;
                if (TryGetProperty(message, "role", out var roleElement)
                    && string.Equals(roleElement.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                {
                    promptPreview = content;
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackMessage))
            {
                promptPreview = fallbackMessage;
                return true;
            }
        }

        foreach (var propertyName in new[] { "prompt", "input", "query" })
        {
            if (TryGetProperty(root, propertyName, out var propertyValue))
            {
                var value = ExtractJsonContent(propertyValue);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    promptPreview = value;
                    return true;
                }
            }
        }

        promptPreview = string.Empty;
        return false;
    }

    public static string ExtractJsonContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Join(Environment.NewLine, element.EnumerateArray()
                .Select(ExtractJsonContent)
                .Where(content => !string.IsNullOrWhiteSpace(content)));
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "text", "content", "value" })
            {
                if (TryGetProperty(element, propertyName, out var propertyValue))
                {
                    var value = ExtractJsonContent(propertyValue);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }

        return string.Empty;
    }

    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    public static string TakePreviewLines(string value)
    {
        var lines = value.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .ToArray();

        var preview = lines.Length == 0 ? value.Trim() : string.Join(Environment.NewLine, lines);
        const int maximumLength = 220;
        return preview.Length <= maximumLength ? preview : $"{preview[..maximumLength]}...";
    }

    public static string GetHistoryStatusClass(ChatHistoryEntry entry)
    {
        var statusCode = GetHistoryStatusCode(entry);
        if (statusCode == 429)
        {
            return "history-rate-limited";
        }

        if (statusCode is >= 200 and < 400)
        {
            return "history-success";
        }

        if (statusCode is >= 400 || string.Equals(entry.Metrics.Status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "history-failed";
        }

        return string.Empty;
    }

    public static int? GetHistoryStatusCode(ChatHistoryEntry entry)
    {
        var status = entry.Metrics.Status;
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var token = status.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var statusCode)
            ? statusCode
            : null;
    }

    public static string FormatHistoryAge(DateTimeOffset value)
    {
        var age = DateTimeOffset.Now - value;
        if (age.TotalMinutes < 1)
        {
            return "Just now";
        }

        if (age.TotalHours < 1)
        {
            var minutes = Math.Max(1, (int)age.TotalMinutes);
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (age.TotalHours < 24)
        {
            var hours = Math.Max(1, (int)age.TotalHours);
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return value.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
