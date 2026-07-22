using System.Globalization;
using System.Text.Json;

namespace chat_tester.Components.Shared;

/// <summary>
/// Interprets raw proxy responses into the <see cref="VisionResultView"/> projection and
/// the human-readable display text used by the investigator page. All members are pure and
/// operate only on the supplied strings/JSON, with no dependency on component state.
/// </summary>
public static class VisionResponseParser
{
    /// <summary>Builds a minimal result view that only carries the supplied message text.</summary>
    public static VisionResultView BuildPlainVisionResult(string message)
    {
        return new VisionResultView
        {
            SummaryText = message,
            RawText = message
        };
    }

    /// <summary>
    /// Parses an observed response body into a structured <see cref="VisionResultView"/>,
    /// falling back to <paramref name="fallbackText"/> for the summary when no structured
    /// vision content is present.
    /// </summary>
    public static VisionResultView BuildVisionResultView(string observed, string fallbackText)
    {
        var view = BuildPlainVisionResult(string.IsNullOrWhiteSpace(fallbackText) ? observed : fallbackText);
        view.RawText = TryFormatJson(observed, out var formatted) ? formatted : observed;

        if (!TryParseJson(observed, out var document))
        {
            return view;
        }

        using (document)
        {
            var root = document.RootElement;
            view.ModelVersion = TryGetProperty(root, "modelVersion", out var modelVersion) ? modelVersion.GetString() ?? string.Empty : string.Empty;

            if (TryGetProperty(root, "captionResult", out var captionResult))
            {
                view.HasCaptionResult = true;
                view.Caption = ReadCaptionItem(captionResult);
            }

            if (TryGetProperty(root, "denseCaptionsResult", out var denseCaptionsResult))
            {
                view.HasDenseCaptionsResult = true;
                view.DenseCaptions.AddRange(ReadCaptionValues(denseCaptionsResult));
            }

            if (TryGetProperty(root, "tagsResult", out var tagsResult))
            {
                view.HasTagsResult = true;
                view.Tags.AddRange(ReadTagValues(tagsResult));
            }

            if (TryGetProperty(root, "objectsResult", out var objectsResult))
            {
                view.HasObjectsResult = true;
                view.Objects.AddRange(ReadObjectValues(objectsResult));
            }

            if (TryGetProperty(root, "peopleResult", out var peopleResult))
            {
                view.HasPeopleResult = true;
                view.People.AddRange(ReadPeopleValues(peopleResult));
            }

            if (TryGetProperty(root, "readResult", out var readResult))
            {
                view.HasReadResult = true;
                view.ReadLines.AddRange(ReadTextLines(readResult));
            }

            if (TryGetProperty(root, "metadata", out var metadata))
            {
                view.HasMetadataResult = true;
                view.Metadata = ReadMetadata(metadata);
            }
        }

        return view;
    }

    /// <summary>
    /// Extracts the best human-readable display text from an observed response, trying the
    /// chat stream parsers first, then vision-specific fields, then formatted JSON.
    /// </summary>
    public static string BuildDisplayResponse(string observed)
    {
        var interpreted = ChatStreamParser.ExtractAllContent(observed);
        if (!string.IsNullOrWhiteSpace(interpreted))
        {
            return interpreted;
        }

        interpreted = ChatStreamParser.ExtractDisplayContent(observed);
        if (!string.IsNullOrWhiteSpace(interpreted))
        {
            return interpreted;
        }

        interpreted = ExtractVisionDisplayContent(observed);
        if (!string.IsNullOrWhiteSpace(interpreted))
        {
            return interpreted;
        }

        return TryFormatJson(observed, out var formatted) ? formatted : observed;
    }

    private static VisionCaptionItem? ReadCaptionItem(JsonElement element)
    {
        var text = TryGetProperty(element, "text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(text) && element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new VisionCaptionItem
        {
            Text = text,
            Confidence = TryGetProperty(element, "confidence", out var confidenceElement) ? ReadDouble(confidenceElement) : null,
            Box = TryGetProperty(element, "boundingBox", out var boxElement) ? ReadBoundingBox(boxElement) : new VisionBoundingBox()
        };
    }

    private static IEnumerable<VisionCaptionItem> ReadCaptionValues(JsonElement section)
    {
        if (!TryGetProperty(section, "values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in values.EnumerateArray())
        {
            var caption = ReadCaptionItem(item);
            if (caption is not null && !string.IsNullOrWhiteSpace(caption.Text))
            {
                yield return caption;
            }
        }
    }

    private static IEnumerable<VisionTagItem> ReadTagValues(JsonElement section)
    {
        if (!TryGetProperty(section, "values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in values.EnumerateArray())
        {
            var tag = ReadTag(item);
            if (!string.IsNullOrWhiteSpace(tag.Name))
            {
                yield return tag;
            }
        }
    }

    private static VisionTagItem ReadTag(JsonElement element)
    {
        return new VisionTagItem
        {
            Name = TryGetProperty(element, "name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
            Confidence = TryGetProperty(element, "confidence", out var confidenceElement) ? ReadDouble(confidenceElement) : null
        };
    }

    private static IEnumerable<VisionDetectionItem> ReadObjectValues(JsonElement section)
    {
        if (!TryGetProperty(section, "values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in values.EnumerateArray())
        {
            var tags = TryGetProperty(item, "tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                ? tagsElement.EnumerateArray().Select(ReadTag).Where(tag => !string.IsNullOrWhiteSpace(tag.Name)).ToList()
                : new List<VisionTagItem>();
            var label = string.Join(", ", tags.Select(tag => tag.Name));
            yield return new VisionDetectionItem
            {
                Label = string.IsNullOrWhiteSpace(label) ? "Object" : label,
                Confidence = tags.FirstOrDefault()?.Confidence,
                Box = TryGetProperty(item, "boundingBox", out var boxElement) ? ReadBoundingBox(boxElement) : new VisionBoundingBox()
            };
        }
    }

    private static IEnumerable<VisionDetectionItem> ReadPeopleValues(JsonElement section)
    {
        if (!TryGetProperty(section, "values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in values.EnumerateArray())
        {
            yield return new VisionDetectionItem
            {
                Label = "Person",
                Confidence = TryGetProperty(item, "confidence", out var confidenceElement) ? ReadDouble(confidenceElement) : null,
                Box = TryGetProperty(item, "boundingBox", out var boxElement) ? ReadBoundingBox(boxElement) : new VisionBoundingBox()
            };
        }
    }

    private static IEnumerable<string> ReadTextLines(JsonElement readResult)
    {
        var lines = new List<string>();
        CollectTextLines(readResult, lines);
        return lines.Distinct(StringComparer.Ordinal).Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static void CollectTextLines(JsonElement element, List<string> lines)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(element, "text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text);
                }
            }

            if (TryGetProperty(element, "content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
            {
                var text = contentElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(text);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectTextLines(property.Value, lines);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTextLines(item, lines);
            }
        }
    }

    private static VisionImageMetadata ReadMetadata(JsonElement metadata)
    {
        return new VisionImageMetadata
        {
            Width = TryGetProperty(metadata, "width", out var widthElement) ? ReadInt(widthElement) : null,
            Height = TryGetProperty(metadata, "height", out var heightElement) ? ReadInt(heightElement) : null
        };
    }

    private static VisionBoundingBox ReadBoundingBox(JsonElement box)
    {
        return new VisionBoundingBox
        {
            X = TryGetProperty(box, "x", out var xElement) ? ReadInt(xElement) : null,
            Y = TryGetProperty(box, "y", out var yElement) ? ReadInt(yElement) : null,
            Width = TryGetProperty(box, "w", out var widthElement) ? ReadInt(widthElement) : null,
            Height = TryGetProperty(box, "h", out var heightElement) ? ReadInt(heightElement) : null
        };
    }

    private static int? ReadInt(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static double? ReadDouble(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static string ExtractVisionDisplayContent(string observed)
    {
        if (!TryParseJson(observed, out var document))
        {
            return string.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            var parts = new List<string>();

            if (TryGetProperty(root, "content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(content.EnumerateArray()
                    .Select(item => TryGetProperty(item, "text", out var text) ? text.GetString() : string.Empty)
                    .Where(text => !string.IsNullOrWhiteSpace(text))!);
            }

            if (TryGetProperty(root, "candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(candidates.EnumerateArray().SelectMany(ExtractGeminiText));
            }

            if (TryGetProperty(root, "captionResult", out var captionResult) && TryGetProperty(captionResult, "text", out var captionText))
            {
                parts.Add("Caption: " + captionText.GetString());
            }

            if (TryGetProperty(root, "tagsResult", out var tagsResult) && TryGetProperty(tagsResult, "values", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                var tagNames = tags.EnumerateArray()
                    .Select(tag => TryGetProperty(tag, "name", out var name) ? name.GetString() : string.Empty)
                    .Where(name => !string.IsNullOrWhiteSpace(name));
                var joinedTags = string.Join(", ", tagNames);
                if (!string.IsNullOrWhiteSpace(joinedTags))
                {
                    parts.Add("Tags: " + joinedTags);
                }
            }

            if (TryGetProperty(root, "readResult", out var readResult) && TryGetProperty(readResult, "content", out var readContent))
            {
                var text = readContent.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add("Read: " + text);
                }
            }

            return string.Join(Environment.NewLine + Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    private static IEnumerable<string> ExtractGeminiText(JsonElement candidate)
    {
        if (!TryGetProperty(candidate, "content", out var content) || !TryGetProperty(content, "parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (TryGetProperty(part, "text", out var text))
            {
                var value = text.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(value) || !value.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryFormatJson(string value, out string formatted)
    {
        formatted = string.Empty;
        if (!TryParseJson(value, out var document))
        {
            return false;
        }

        using (document)
        {
            formatted = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
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
}
