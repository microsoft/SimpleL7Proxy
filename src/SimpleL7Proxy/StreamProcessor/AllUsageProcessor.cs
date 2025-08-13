
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
        /// Processes the last line to extract comprehensive statistics from the JSON response.
        /// Recursively extracts all fields using dot notation for nested objects.
        /// </summary>
        /// <param name="lastLine">The last line from the stream.</param>
        protected override void ProcessLastLine(string lastLine)
        {
            try
            {
                var jsonNode = ParseJsonLine(lastLine);
                if (jsonNode != null)
                {
                    // Extract all fields from the entire JSON response
                    ExtractAllFields(jsonNode, "");

                    // Ensure usage fields have defaults if not present
                    if (!data.ContainsKey("usage.completion_tokens"))
                        data["usage.completion_tokens"] = "0";
                    if (!data.ContainsKey("usage.prompt_tokens"))
                        data["usage.prompt_tokens"] = "0";
                    if (!data.ContainsKey("usage.total_tokens"))
                        data["usage.total_tokens"] = "0";
                }
            }
            catch (Exception ex)
            {
                // Not able to parse the last line, log the error
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
                eventData[kvp.Key] = kvp.Value;
            }

            // For backward compatibility, also set the legacy field names if they exist
            if (data.ContainsKey("usage.completion_tokens"))
                eventData["Completion_Tokens"] = data["usage.completion_tokens"];
            if (data.ContainsKey("usage.prompt_tokens"))
                eventData["Prompt_Tokens"] = data["usage.prompt_tokens"];
            if (data.ContainsKey("usage.total_tokens"))
                eventData["Total_Tokens"] = data["usage.total_tokens"];

            // Extract finish reason from choices if available
            var finishReasonKey = data.Keys.FirstOrDefault(k => k.EndsWith(".finish_reason"));
            if (!string.IsNullOrEmpty(finishReasonKey))
                eventData["Finish_Reason"] = data[finishReasonKey];

            // Count total choices
            var choicesCount = data.Keys.Count(k => k.StartsWith("choices[") && k.Contains("].index"));
            if (choicesCount > 0)
                eventData["Choices_Count"] = choicesCount.ToString();

            // Extract any additional usage details that might be present
            var usageKeys = data.Keys.Where(k => k.StartsWith("usage.") && !k.Contains("completion_tokens") && !k.Contains("prompt_tokens") && !k.Contains("total_tokens"));
            foreach (var key in usageKeys)
            {
                var legacyKey = key.Substring(6); // Remove "usage." prefix
                var formattedKey = string.Join("_", legacyKey.Split('.', '[', ']').Where(s => !string.IsNullOrEmpty(s)).Select(s => char.ToUpper(s[0]) + s.Substring(1).ToLower()));
                eventData[$"Usage_{formattedKey}"] = data[key];
            }
        }

        /// <summary>
        /// Recursively extracts all fields from a JSON node, preserving hierarchy with dot notation.
        /// </summary>
        /// <param name="node">The JSON node to extract fields from.</param>
        /// <param name="prefix">The prefix for the field names (hierarchy path).</param>
        private void ExtractAllFields(JsonNode? node, string prefix)
        {
            if (node == null) return;

            switch (node)
            {
                case JsonObject jsonObject:
                    foreach (var kvp in jsonObject)
                    {
                        var fieldName = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";
                        ExtractAllFields(kvp.Value, fieldName);
                    }
                    break;

                case JsonArray jsonArray:
                    for (int i = 0; i < jsonArray.Count; i++)
                    {
                        var fieldName = string.IsNullOrEmpty(prefix) ? $"[{i}]" : $"{prefix}[{i}]";
                        ExtractAllFields(jsonArray[i], fieldName);
                    }
                    break;

                case JsonValue jsonValue:
                    // Extract the actual value and store it as a string
                    var value = jsonValue.GetValue<object>();
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        data[prefix] = value?.ToString() ?? "null";
                    }
                    break;

                default:
                    if (!string.IsNullOrEmpty(prefix))
                    {
                        data[prefix] = node.ToString();
                    }
                    break;
            }
        }
    }
}