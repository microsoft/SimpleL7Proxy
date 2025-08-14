
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
        /// Processes the last lines to extract OpenAI-specific statistics.
        /// Looks for usage.completion_tokens, usage.prompt_tokens, and usage.total_tokens.
        /// </summary>
        /// <param name="lastLines">Array of the last significant lines from the stream.</param>
        /// <param name="primaryLine">The primary line to process.</param>

        protected override void ProcessLastLines(string[] lastLines, string primaryLine)
        {
            try
            {
                var jsonNode = ParseJsonLine(primaryLine);
                if (jsonNode?["usage"] is JsonObject usage)
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
            catch (Exception ex)
            {
                data["ParseError"] = ex.Message;
            }
        }

        /// <summary>
        /// Populates event data with OpenAI-specific statistics.
        /// </summary>
        protected override void PopulateEventData(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // Transfer the specific OpenAI fields we care about
            if (data.TryGetValue("Completion_Tokens", out var completionTokens))
                eventData["Usage.Completion_Tokens"] = completionTokens;
            if (data.TryGetValue("Prompt_Tokens", out var promptTokens))
                eventData["Usage.Prompt_Tokens"] = promptTokens;
            if (data.TryGetValue("Total_Tokens", out var totalTokens))
                eventData["Usage.Total_Tokens"] = totalTokens;

            // Include error information if present
            if (data.TryGetValue("ParseError", out var parseError))
                eventData["Usage.ParseError"] = parseError;
        }
    }
}