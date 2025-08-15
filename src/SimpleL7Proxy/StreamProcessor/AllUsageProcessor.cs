
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor implementation that extracts comprehensive usage statistics
    /// from JSON streaming responses, capturing all fields in the response.
    /// </summary>
    public class AllUsageProcessor : JsonStreamProcessor
    {
        /// <summary>
        /// Processes the last lines to extract comprehensive statistics from the JSON response.
        /// Recursively extracts all fields using dot notation for nested objects.
        /// </summary>
        /// <param name="lastLines">Array of the last significant lines from the stream.</param>
        /// <param name="primaryLine">The primary line to process.</param>
        /// <summary>
        /// Processes the last lines to extract comprehensive statistics from the JSON response.
        /// Extracts all fields using dot notation for nested objects.
        /// </summary>
        /// <param name="lastLines">Array of the last significant lines from the stream.</param>
        /// <param name="primaryLine">The primary line to process.</param>
        protected override void ProcessLastLines(string[] lastLines, string primaryLine)
        {
            try
            {
                var jsonNode = ParseJsonLine(primaryLine);
                if (jsonNode != null)
                {
                    ExtractAllFields(jsonNode, "");

                    // Set defaults for required usage fields
                    data.TryAdd("Completion_Tokens", "0");
                    data.TryAdd("Prompt_Tokens", "0");
                    data.TryAdd("Total_Tokens", "0");
                }
            }
            catch (Exception ex)
            {
                data["ParseError"] = ex.Message;
            }
        }

        /// <summary>
        /// Populates event data with comprehensive statistics and provides backward compatibility.
        /// </summary>
        protected override void PopulateEventData(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // Copy all captured data to the event data
            foreach (var kvp in data)
            {
                // Convert key to PascalCase: usage.foo_bar => Usage.Foo_Bar
                var convertedKey = ConvertToPascalCase(kvp.Key);
                eventData[convertedKey] = kvp.Value;
            }
        }

        /// <summary>
        /// Simplified extraction for simple JSON with few fields and 1-2 layers deep.
        /// Converts all values to strings for consistent data handling.
        /// </summary>
        /// <param name="node">The JSON node to extract fields from.</param>
        /// <param name="prefix">The prefix for the field names (hierarchy path).</param>
        private void ExtractAllFields(JsonNode? node, string prefix)
        {
            if (node is not JsonObject jsonObject) return;

            foreach (var (key, value) in jsonObject)
            {
                if (value == null) continue;

                var fieldName = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";

                switch (value)
                {
                    case JsonValue jsonValue:
                        data[fieldName] = jsonValue.ToString();
                        break;

                    case JsonObject nestedObject:
                        foreach (var (nestedKey, nestedValue) in nestedObject)
                        {
                            if (nestedValue is JsonValue nestedJsonValue)
                                data[$"{fieldName}.{nestedKey}"] = nestedJsonValue.ToString();
                        }
                        break;

                    case JsonArray jsonArray:
                        for (int i = 0; i < jsonArray.Count; i++)
                        {
                            if (jsonArray[i] is JsonObject arrayObject)
                            {
                                foreach (var (arrayKey, arrayValue) in arrayObject)
                                {
                                    if (arrayValue is JsonValue arrayJsonValue)
                                        data[$"{fieldName}[{i}].{arrayKey}"] = arrayJsonValue.ToString();
                                }
                            }
                        }
                        break;
                }
            }
        }
    }
}