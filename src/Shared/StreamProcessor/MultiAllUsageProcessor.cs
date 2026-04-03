
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor implementation that extracts comprehensive usage statistics
    /// from JSON streaming responses, capturing all fields in the response.
    /// </summary>
    public class MultiLineAllUsageProcessor : JsonStreamProcessor
    {
        // Pre-compiled regex for extracting usage/usageMetadata JSON blocks from streaming responses
        private static readonly Regex s_usageJsonRegex = new(
            @"""(?:[uU]sage|[uU]sage[mM]etadata)"":\s*(\{(?:[^{}]|(?<open>\{)|(?<-open>\}))*(?(open)(?!))\})",
            RegexOptions.Singleline | RegexOptions.Compiled);

        protected override int MaxLines => 100;
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

            var match = s_usageJsonRegex.Match(input);
            var jsonBlock = String.Empty;

            if (match.Success)
            {
                jsonBlock = @"{""usage"": " + match.Groups[1].Value + @"}"; // This is the JSON object after "usage"

                // Extract the JSON block
                //jsonBlock = string.Join("\n", lastLines[startIndex..(endIndex + 1)]);
                try
                {
                    var jsonNode = ParseJsonLine(jsonBlock);
                    if (jsonNode != null)
                    {
                        ExtractAllFields(jsonNode, "Usage");
                    }
                }
                catch (Exception ex)
                {
                    data["ParseError"] = ex.Message;
                }
            }
            else
            {
                // Console.WriteLine("Couldn't parse it");
            }


        }

        /// <summary>
        /// Populates event data with comprehensive statistics and provides backward compatibility.
        /// </summary>
        protected override void PopulateEventData(IDictionary<string, string> eventData, HttpResponseHeaders headers)
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