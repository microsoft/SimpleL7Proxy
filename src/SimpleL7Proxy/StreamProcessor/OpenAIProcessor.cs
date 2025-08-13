
using System.Text.Json.Nodes;
using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor implementation for OpenAI-specific stream processing.
    /// Extracts token usage statistics from the last line of OpenAI streaming responses.
    /// </summary>
    public class OpenAIProcessor : JsonStreamProcessor
    {
        /// <summary>
        /// Processes the last line to extract OpenAI-specific statistics.
        /// Looks for usage.completion_tokens, usage.prompt_tokens, and usage.total_tokens.
        /// </summary>
        /// <param name="lastLine">The last line from the stream.</param>
        protected override void ProcessLastLine(string lastLine)
        {
            try
            {
                var jsonNode = ParseJsonLine(lastLine);
                if (jsonNode != null)
                {
                    // Extract usage statistics
                    var usage = jsonNode["usage"];
                    if (usage != null)
                    {
                        data["Completion_Tokens"] = ExtractTokenCount(usage, "completion_tokens");
                        data["Prompt_Tokens"] = ExtractTokenCount(usage, "prompt_tokens");
                        data["Total_Tokens"] = ExtractTokenCount(usage, "total_tokens");
                    }
                    else
                    {
                        // Set defaults if usage is not present
                        data["Completion_Tokens"] = "0";
                        data["Prompt_Tokens"] = "0";
                        data["Total_Tokens"] = "0";
                    }
                }
            }
            catch (Exception ex)
            {
                // Not able to parse the last line, log the error
                data["ParseError"] = ex.Message;
            }
        }

        /// <summary>
        /// Populates event data with OpenAI-specific statistics.
        /// </summary>
        protected override void PopulateEventData(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // Transfer the specific OpenAI fields we care about
            if (data.ContainsKey("Completion_Tokens"))
                eventData["Completion_Tokens"] = data["Completion_Tokens"];
            if (data.ContainsKey("Prompt_Tokens"))
                eventData["Prompt_Tokens"] = data["Prompt_Tokens"];
            if (data.ContainsKey("Total_Tokens"))
                eventData["Total_Tokens"] = data["Total_Tokens"];

            // Also include any error information
            if (data.ContainsKey("ParseError"))
                eventData["ParseError"] = data["ParseError"];
            if (data.ContainsKey("LastError"))
                eventData["LastError"] = data["LastError"];
            if (data.ContainsKey("LastLineProcessingError"))
                eventData["LastLineProcessingError"] = data["LastLineProcessingError"];
        }
    }
}