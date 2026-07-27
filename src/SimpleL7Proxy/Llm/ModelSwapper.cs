using System.Text.Json;

namespace SimpleL7Proxy.Llm;

public class ModelSwapper
{
    private readonly ILogger<ModelSwapper> _logger;

    public ModelSwapper(ILogger<ModelSwapper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Walks a JSON request body once to capture the top-level "model" property onto the request.
    /// When <paramref name="modelOverride"/> is provided it wins over the body value, and the same pass
    /// rewrites the body so the backend receives the overridden model (adding it when absent). The
    /// <see cref="Utf8JsonWriter"/> is only allocated when an override is present, so the common
    /// detect-only path stays allocation-free. On malformed JSON, sets a sentinel value only when no
    /// model has been captured yet.
    /// </summary>
    /// <returns>The request body bytes, rewritten when an override was applied; otherwise the original bytes.</returns>
    public static ReadOnlyMemory<byte> ValidateModel(RequestData request, ReadOnlyMemory<byte> bodyBytes, string? modelOverride = null)
    {
        bool hasOverride = !string.IsNullOrWhiteSpace(modelOverride);
        System.Buffers.ArrayBufferWriter<byte>? buffer = null;
        Utf8JsonWriter? writer = null;

        try
        {
            var reader = new Utf8JsonReader(bodyBytes.Span, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return bodyBytes;
            }

            string? sourceModel = null;
            if (hasOverride)
            {
                while (reader.Read())
                {
                    if (reader.CurrentDepth == 1
                        && reader.TokenType == JsonTokenType.PropertyName
                        && reader.ValueTextEquals("model"u8)
                        && reader.Read()
                        && reader.TokenType == JsonTokenType.String)
                    {
                        sourceModel = reader.GetString();
                        break;
                    }
                }

                request.Model = modelOverride!;
                reader = new Utf8JsonReader(bodyBytes.Span, isFinalBlock: true, state: default);
                reader.Read();

                buffer = new System.Buffers.ArrayBufferWriter<byte>(bodyBytes.Length + modelOverride!.Length + 16);
                writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                writer.WriteStartObject();
            }

            var (fieldsToRemove, fieldsToRename) = hasOverride && !string.IsNullOrWhiteSpace(sourceModel)
                ? ModelMap.Get(sourceModel, modelOverride!)
                : (FieldRemovalMap.Empty, FieldRenameMap.Empty);

            bool handledModel = false;
            while (reader.Read() && !(reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0))
            {
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("model"u8))
                    {
                        if (writer != null)
                        {
                            writer.WriteString("model", modelOverride);
                            handledModel = true;
                            reader.Read();
                            reader.Skip();
                            continue;
                        }

                        if (reader.Read() && reader.TokenType == JsonTokenType.String)
                        {
                            var model = reader.GetString();
                            if (!string.IsNullOrWhiteSpace(model))
                            {
                                request.Model = model;
                            }
                        }
                        break;
                    }

                    if (writer != null)
                    {
                        string propertyName = reader.GetString()!;
                        if (fieldsToRemove.Contains(propertyName))
                        {
                            reader.Read();
                            reader.Skip();
                            continue;
                        }

                        writer.WritePropertyName(fieldsToRename.GetValueOrDefault(propertyName, propertyName));
                        continue;
                    }

                    reader.Read();
                    reader.Skip();
                    continue;
                }

                if (writer != null)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                        case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                        case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                        case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                        case JsonTokenType.PropertyName: writer.WritePropertyName(reader.GetString()!); break;
                        case JsonTokenType.String: writer.WriteStringValue(reader.GetString()); break;
                        case JsonTokenType.Number: writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true); break;
                        case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                        case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                        case JsonTokenType.Null: writer.WriteNullValue(); break;
                    }
                }
            }

            if (writer != null)
            {
                if (!handledModel)
                {
                    writer.WriteString("model", modelOverride);
                }

                writer.WriteEndObject();
                writer.Flush();

                var rewritten = buffer!.WrittenMemory;
                request.setBody(rewritten);
                return rewritten;
            }
        }
        catch (JsonException)
        {
            if (!hasOverride && string.IsNullOrEmpty(request.Model))
            {
                request.Model = "Error parsing model";
            }
        }
        finally
        {
            writer?.Dispose();
        }

        return bodyBytes;
    }

}
