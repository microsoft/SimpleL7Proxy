using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CompanionApp.Components.Shared;

namespace CompanionApp.Observability;

/// <summary>Normalizes the usage headers and final response observed on a request.</summary>
public static class ObservedUsageParser
{
    /// <summary>Reads nonnegative usage scalars, keeps maximum duplicate values, and derives a missing token total.</summary>
    public static ImmutableDictionary<string, long> Parse(MultiRequestStatusItem item)
    {
        var metrics = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        static string NormalizeUsageMetricKey(string rawKey)
        {
            static string NormalizeSegment(string segment)
            {
                var builder = new System.Text.StringBuilder(segment.Length + 8);
                for (var i = 0; i < segment.Length; i++)
                {
                    var current = segment[i];
                    if (current == '-' || current == ' ')
                    {
                        builder.Append('_');
                        continue;
                    }

                    if (current == '_')
                    {
                        builder.Append('_');
                        continue;
                    }

                    if (char.IsUpper(current) && i > 0 && builder.Length > 0 && builder[^1] != '_')
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(current));
                }

                return builder.ToString().Trim('_');
            }

            var trimmed = rawKey.Trim();
            if (trimmed.StartsWith("Usage.", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["Usage.".Length..];
            }
            else if (string.Equals(trimmed, "Usage", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = string.Empty;
            }

            var normalizedSegments = trimmed
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeSegment)
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            return normalizedSegments.Length == 0
                ? "usage"
                : $"usage.{string.Join('.', normalizedSegments)}";
        }

        static void AddMetric(IDictionary<string, long> values, string rawKey, long value)
        {
            if (value < 0)
            {
                return;
            }

            var normalizedKey = NormalizeUsageMetricKey(rawKey);
            if (normalizedKey.Length == 0)
            {
                return;
            }

            values[normalizedKey] = values.TryGetValue(normalizedKey, out var existingValue)
                ? Math.Max(existingValue, value)
                : value;
        }

        static void ReadUsageHeaders(string? headers, IDictionary<string, long> values)
        {
            if (string.IsNullOrWhiteSpace(headers))
            {
                return;
            }

            var lines = headers
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                if (!key.StartsWith("Usage.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawValue = line[(separatorIndex + 1)..].Trim();
                if (TryParseLong(rawValue, out var parsedValue))
                {
                    AddMetric(values, key, parsedValue);
                }
            }
        }

        static void ReadUsageJson(JsonElement element, string prefix, IDictionary<string, long> values)
        {
            foreach (var property in element.EnumerateObject())
            {
                var path = string.IsNullOrWhiteSpace(prefix)
                    ? property.Name
                    : $"{prefix}.{property.Name}";

                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    ReadUsageJson(property.Value, path, values);
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var numericValue))
                {
                    AddMetric(values, path, numericValue);
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.String && TryParseLong(property.Value.GetString(), out var stringValue))
                {
                    AddMetric(values, path, stringValue);
                }
            }
        }

        ReadUsageHeaders(item.ResponseHeadersText, metrics);
        ReadUsageHeaders(item.RequestHeadersText, metrics);

        var responseBody = item.ResponseBody?.Trim();
        if (!string.IsNullOrWhiteSpace(responseBody)
            && (responseBody.StartsWith("{", StringComparison.Ordinal) || responseBody.StartsWith("[", StringComparison.Ordinal)))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("usage", out var usageElement)
                        && usageElement.ValueKind == JsonValueKind.Object)
                    {
                        ReadUsageJson(usageElement, "usage", metrics);
                    }
                    else if (document.RootElement.TryGetProperty("response", out var responseElement)
                        && responseElement.ValueKind == JsonValueKind.Object
                        && responseElement.TryGetProperty("usage", out var nestedUsageElement)
                        && nestedUsageElement.ValueKind == JsonValueKind.Object)
                    {
                        ReadUsageJson(nestedUsageElement, "usage", metrics);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (!metrics.ContainsKey("usage.total_tokens"))
        {
            var hasInput = metrics.TryGetValue("usage.input_tokens", out var inputTokens)
                || metrics.TryGetValue("usage.prompt_tokens", out inputTokens);
            var hasOutput = metrics.TryGetValue("usage.output_tokens", out var outputTokens)
                || metrics.TryGetValue("usage.completion_tokens", out outputTokens);

            if (hasInput && hasOutput)
            {
                metrics["usage.total_tokens"] = inputTokens + outputTokens;
            }
        }

        return metrics.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryParseLong(string? value, out long parsed)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }

}
