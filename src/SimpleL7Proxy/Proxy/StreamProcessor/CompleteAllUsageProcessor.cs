
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
    public class CompleteAllUsageProcessor : JsonStreamProcessor
    {
        // Pre-compiled regex for extracting usage/usageMetadata JSON blocks from streaming responses
        private static readonly Regex s_usageJsonRegex = new(
            @"""(?:[uU]sage|[uU]sage[mM]etadata)"":\s*(\{(?:[^{}]|(?<open>\{)|(?<-open>\}))*(?(open)(?!))\})",
            RegexOptions.Singleline | RegexOptions.Compiled);

        protected override int MaxLines => 100;
        protected override int MinLineLength => 1;
        protected override bool CaptureAllLines => true; // Capture all lines for Anthropic responses

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

            var matches = s_usageJsonRegex.Matches(input);
            int count=0;

            if (matches.Count > 0)
            {
                foreach (Match match in matches)
                {
                    var jsonBlock = @"{""usage"": " + match.Groups[1].Value + @"}";

                    try
                    {
                        var jsonNode = ParseJsonLine(jsonBlock);
                        if (jsonNode != null)
                        {
                            count++;
                            ExtractAllFields(jsonNode, "Usage");
                        }
                    }
                    catch (Exception ex)
                    {
                        data["ParseError"] = ex.Message;
                    }
                }
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