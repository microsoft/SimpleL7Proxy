
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Events;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor implementation that extracts comprehensive usage statistics
    /// from JSON streaming responses, capturing all fields in the response.
    /// </summary>
    public class MultiLineAllUsageProcessor : JsonStreamProcessor
    {

        protected override int MaxLines => 30;
        protected override int MinLineLength => 1;

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

            //  the usage JSON is spread over multiple lines.  We need to know where it starts and end.
            int startIndex = Array.IndexOf(lastLines, primaryLine);
            var input = string.Join(" ", lastLines[startIndex..]);

            // Use a regex to extract the json for either usage or usageMetadata.
            var jsonPattern = @"""(?:[uU]sage|[uU]sage[mM]etadata)"":\s*(\{(?:[^{}]|(?<open>\{)|(?<-open>\}))*\}(?(open)(?!)))";
            var match = Regex.Match(input, jsonPattern, RegexOptions.Singleline);
            var jsonBlock = String.Empty;

            if (match.Success)
            {
                jsonBlock = @"{""usage"": " + match.Groups[1].Value + @"}"; // This is the JSON object after "usage"
            }
            else
            {
                Console.WriteLine("Couldn't parse it");
            }

            // Extract the JSON block
            //jsonBlock = string.Join("\n", lastLines[startIndex..(endIndex + 1)]);
            try
            {
                var jsonNode = ParseJsonLine(jsonBlock);
                if (jsonNode != null)
                {
                    ExtractAllFields(jsonNode, "Usage");

                    // // Set defaults for required usage fields
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