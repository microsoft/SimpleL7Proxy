
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
                    ExtractAllFields(jsonNode, "Usage");

                    // Set defaults for required usage fields
                    // data.TryAdd("Completion_Tokens", "0");
                    // data.TryAdd("Prompt_Tokens", "0");
                    // data.TryAdd("Total_Tokens", "0");
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

    }
}