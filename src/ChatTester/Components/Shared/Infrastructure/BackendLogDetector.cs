using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>
/// Detects and extracts SimpleL7Proxy backend log entries. A backend log may be delivered
/// either as a response header (for example <c>backendLog: ...</c> or <c>x-backendLog: ...</c>)
/// or embedded as a <c>backendLog</c> property inside a JSON response body.
/// </summary>
public static class BackendLogDetector
{
    /// <summary>
    /// Returns <see langword="true"/> when a backend log can be found in either the
    /// response headers or the response body.
    /// </summary>
    public static bool HasBackendLog(string? responseHeaders, string? responseBody) =>
        ExtractFromHeaders(responseHeaders).Count > 0 || ExtractFromBody(responseBody).Count > 0;

    /// <summary>
    /// Extracts every backend log value carried by a backend log response header.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromHeaders(string? responseHeaders)
    {
        var logs = new List<string>();
        if (string.IsNullOrWhiteSpace(responseHeaders))
        {
            return logs;
        }

        foreach (var line in responseHeaders.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            if (!IsBackendLogHeaderName(name))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                logs.Add(value);
            }
        }

        return logs;
    }

    /// <summary>
    /// Extracts every backend log value embedded as a <c>backendLog</c> JSON property inside the
    /// response body. Tolerates bodies that prefix the JSON payload with a plain-text message.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromBody(string? responseBody)
    {
        var logs = new List<string>();
        if (TryLocateJson(responseBody, out var document))
        {
            using (document)
            {
                CollectBackendLogs(document.RootElement, logs);
            }
        }

        return logs;
    }

    /// <summary>
    /// Determines whether a response header name carries a backend log value.
    /// </summary>
    public static bool IsBackendLogHeaderName(string name) =>
        string.Equals(name, "backendLog", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("-backendLog", StringComparison.OrdinalIgnoreCase);

    private static bool TryLocateJson(string? body, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (TryParseJson(body, out document))
        {
            return true;
        }

        var jsonStart = body.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        return TryParseJson(body[jsonStart..], out document);
    }

    private static bool TryParseJson(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            document = null!;
            return false;
        }
    }

    private static void CollectBackendLogs(JsonElement element, List<string> logs)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("backendLog") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        logs.Add(value);
                    }
                }
                else
                {
                    CollectBackendLogs(property.Value, logs);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectBackendLogs(item, logs);
            }
        }
    }
}
