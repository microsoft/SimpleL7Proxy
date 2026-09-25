using System.Text.Json.Nodes;
using System.Net.Http.Headers;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Stream processor for OpenAI/Azure OpenAI streaming responses that extracts the token
    /// usage and content-safety signals needed for Tokenomics accounting: prompt, completion,
    /// and cached token counts, plus jailbreak detection and content-filter flags.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="OpenAIProcessor"/>, this processor captures every line of the stream
    /// (<see cref="CaptureAllLines"/> is true) because the jailbreak signal is only present in
    /// the prompt filter results of the first streamed chunk, and content-filter results are
    /// spread across every chunk. Capturing all lines increases per-request memory usage, so
    /// this processor should be selected only when Tokenomics accounting requires these signals.
    /// </remarks>
    public class TokenomicsProcessor : JsonStreamProcessor
    {
        protected override bool CaptureAllLines => true;

        /// <summary>
        /// Extracts token usage and content-safety signals from the captured stream lines.
        /// </summary>
        /// <param name="lastLines">Every line captured from the stream (see <see cref="CaptureAllLines"/>).</param>
        /// <param name="primaryLine">The line the base class selected as most likely to contain
        /// usage data. Not used here: providers that emit "usage": null on every delta chunk can
        /// cause the base class to select an earlier chunk instead of the final one, so this
        /// processor re-derives the correct usage object directly from <paramref name="lastLines"/>.</param>
        protected override void ProcessLastLines(string[] lastLines, string primaryLine)
        {
            try
            {
                JsonObject? usage = null;
                for (int i = lastLines.Length - 1; i >= 0; i--)
                {
                    if (ParseJsonLine(lastLines[i])?["usage"] is JsonObject usageObject)
                    {
                        usage = usageObject;
                        break;
                    }
                }

                if (usage != null)
                {
                    data["Completion_Tokens"] = ExtractTokenCount(usage, "completion_tokens");
                    data["Prompt_Tokens"] = ExtractTokenCount(usage, "prompt_tokens");
                    data["Total_Tokens"] = ExtractTokenCount(usage, "total_tokens");
                    data["Cached_Tokens"] = ExtractTokenCount(usage["prompt_tokens_details"], "cached_tokens");
                }
                else
                {
                    data["Completion_Tokens"] = "0";
                    data["Prompt_Tokens"] = "0";
                    data["Total_Tokens"] = "0";
                    data["Cached_Tokens"] = "0";
                }

                var jailbreakDetected = false;
                var contentFiltered = false;

                foreach (var line in lastLines)
                {
                    var lineNode = ParseJsonLine(line);
                    if (lineNode == null) continue;

                    if (!jailbreakDetected && ContainsJailbreakDetected(lineNode))
                    {
                        jailbreakDetected = true;
                    }

                    if (!contentFiltered && ContainsFilteredContent(lineNode))
                    {
                        contentFiltered = true;
                    }

                    if (jailbreakDetected && contentFiltered)
                    {
                        break;
                    }
                }

                data["Is_Jailbreak_Detected"] = jailbreakDetected.ToString();
                data["Is_Content_Filtered"] = contentFiltered.ToString();
            }
            catch (Exception ex)
            {
                data["ParseError"] = ex.Message;
            }
        }

        /// <summary>
        /// Populates event data with Tokenomics-specific statistics.
        /// </summary>
        protected override void PopulateEventData(IDictionary<string, string> eventData, HttpResponseHeaders headers)
        {
            if (data.TryGetValue("Completion_Tokens", out var completionTokens))
                eventData["Usage.Completion_Tokens"] = completionTokens;
            if (data.TryGetValue("Prompt_Tokens", out var promptTokens))
                eventData["Usage.Prompt_Tokens"] = promptTokens;
            if (data.TryGetValue("Total_Tokens", out var totalTokens))
                eventData["Usage.Total_Tokens"] = totalTokens;
            if (data.TryGetValue("Cached_Tokens", out var cachedTokens))
                eventData["Usage.Cached_Tokens"] = cachedTokens;
            if (data.TryGetValue("Is_Jailbreak_Detected", out var jailbreakDetected))
                eventData["Usage.Is_Jailbreak_Detected"] = jailbreakDetected;
            if (data.TryGetValue("Is_Content_Filtered", out var contentFiltered))
                eventData["Usage.Is_Content_Filtered"] = contentFiltered;

            if (data.TryGetValue("ParseError", out var parseError))
                eventData["Usage.ParseError"] = parseError;
        }

        /// <summary>
        /// Checks the prompt filter results for a detected jailbreak (prompt injection) attempt.
        /// </summary>
        private static bool ContainsJailbreakDetected(JsonNode? jsonNode)
        {
            if (jsonNode?["prompt_filter_results"] is not JsonArray promptFilterResults)
            {
                return false;
            }

            foreach (var entry in promptFilterResults)
            {
                if (entry?["content_filter_results"]?["jailbreak"]?["detected"] is JsonValue detectedValue
                    && detectedValue.TryGetValue<bool>(out var detected)
                    && detected)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks the prompt and completion content filter categories for any category flagged
        /// as filtered (e.g. hate, self-harm, sexual, violence, or protected material).
        /// </summary>
        private static bool ContainsFilteredContent(JsonNode? jsonNode)
        {
            return HasFilteredCategory(jsonNode?["prompt_filter_results"] as JsonArray, "content_filter_results")
                || HasFilteredCategory(jsonNode?["choices"] as JsonArray, "content_filter_results");
        }

        private static bool HasFilteredCategory(JsonArray? entries, string contentFilterField)
        {
            if (entries == null)
            {
                return false;
            }

            foreach (var entry in entries)
            {
                if (entry?[contentFilterField] is not JsonObject contentFilterResults)
                {
                    continue;
                }

                foreach (var (_, categoryNode) in contentFilterResults)
                {
                    if (categoryNode?["filtered"] is JsonValue filteredValue
                        && filteredValue.TryGetValue<bool>(out var filtered)
                        && filtered)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
