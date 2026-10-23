using System.Text;
using SimpleL7Proxy.StreamProcessor;
using SimpleL7Proxy.Tokenomics.Llm;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class UsageStreamProcessorTests
{
    [TestMethod]
    public async Task TokenomicsProcessor_ExtractsPrettyPrintedOpenAiUsageAfterPreamble()
    {
        const string responseBody = """
            Submit search feedback
            {
              "usage": {
                "prompt_tokens": 71,
                "completion_tokens": 200,
                "total_tokens": 271,
                "prompt_tokens_details": {
                  "cached_tokens": 0
                }
              },
              "prompt_filter_results": [
                {
                  "content_filter_results": {
                    "jailbreak": { "detected": true },
                    "hate": { "filtered": true }
                  }
                }
              ]
            }
            """;

        var (output, stats) = await ProcessAsync(responseBody);

        Assert.AreEqual(responseBody, output);
        Assert.AreEqual("71", stats["Usage.Prompt_Tokens"]);
        Assert.AreEqual("200", stats["Usage.Completion_Tokens"]);
        Assert.AreEqual("271", stats["Usage.Total_Tokens"]);
        Assert.AreEqual("0", stats["Usage.Cached_Tokens"]);
        Assert.AreEqual("True", stats["Usage.Available"]);
        Assert.AreEqual("True", stats["Usage.Is_Jailbreak_Detected"]);
        Assert.AreEqual("True", stats["Usage.Is_Content_Filtered"]);
        Assert.IsFalse(stats.ContainsKey("Usage.ParseError"));
    }

    [TestMethod]
    public async Task TokenomicsProcessor_UsesFinalNonNullSseUsage()
    {
        const string responseBody = """
            data: {"choices":[],"usage":null}

            data: {"choices":[],"usage":{"prompt_tokens":58,"completion_tokens":1000,"total_tokens":1058,"prompt_tokens_details":{"cached_tokens":4}}}

            data: [DONE]

            """;

        var (output, stats) = await ProcessAsync(responseBody);

        Assert.AreEqual(responseBody, output);
        Assert.AreEqual("58", stats["Usage.Prompt_Tokens"]);
        Assert.AreEqual("1000", stats["Usage.Completion_Tokens"]);
        Assert.AreEqual("1058", stats["Usage.Total_Tokens"]);
        Assert.AreEqual("4", stats["Usage.Cached_Tokens"]);
        Assert.IsFalse(stats.ContainsKey("Usage.ParseError"));
    }

    [TestMethod]
    public async Task TokenomicsProcessor_EmitsNativeGeminiFieldsForModelNormalization()
    {
        const string responseBody = """
            {
              "usageMetadata": {
                "promptTokenCount": 11,
                "cachedContentTokenCount": 2,
                "candidatesTokenCount": 7,
                "totalTokenCount": 21,
                "thoughtsTokenCount": 3
              }
            }
            """;

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage("gemini-2.5-pro", stats);

        Assert.AreEqual(11, usage.InputTokens);
        Assert.AreEqual(10, usage.OutputTokens);
        Assert.AreEqual(2, usage.CachedTokens);
        Assert.AreEqual(21, usage.TotalTokens);
        Assert.AreEqual("True", stats["Usage.Available"]);
    }

    [TestMethod]
    public async Task ModelPreferredNormalization_FallsBackForOpenAiCompatibleGeminiResponse()
    {
        const string responseBody = """
            {
              "model": "google/gemini-2.5-pro",
              "usage": {
                "prompt_tokens": 8,
                "completion_tokens": 750,
                "total_tokens": 758
              }
            }
            """;

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage("gemini-2.5-pro", stats);

        Assert.AreEqual(UsageProvider.OpenAI, usage.Provider);
        Assert.IsTrue(usage.UsageAvailable);
        Assert.AreEqual(8, usage.InputTokens);
        Assert.AreEqual(750, usage.OutputTokens);
        Assert.AreEqual(758, usage.TotalTokens);
    }

    [TestMethod]
    public async Task TokenomicsProcessor_EmitsAnthropicSseFieldsForModelNormalization()
    {
        const string responseBody = """
            event: message_start
            data: {"type":"message_start","message":{"model":"claude-sonnet","id":"msg-1","usage":{"input_tokens":10,"cache_creation_input_tokens":4,"cache_read_input_tokens":3,"output_tokens":1}}}

            event: message_delta
            data: {"type":"message_delta","usage":{"output_tokens":35}}

            """;

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage(UsageProvider.Anthropic, stats);

        Assert.AreEqual(17, usage.InputTokens);
        Assert.AreEqual(35, usage.OutputTokens);
        Assert.AreEqual(3, usage.CachedTokens);
        Assert.AreEqual("True", stats["Usage.Available"]);
    }

    [TestMethod]
    public async Task TokenomicsProcessor_EmitsBedrockFieldsForModelNormalization()
    {
        const string responseBody = """
            {
              "metadata": {
                "usage": {
                  "inputTokens": 13,
                  "outputTokens": 21,
                  "totalTokens": 34,
                  "cacheReadInputTokens": 5
                }
              }
            }
            """;

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage("amazon.nova-pro", stats);

        Assert.AreEqual(UsageProvider.Bedrock, usage.Provider);
        Assert.AreEqual(13, usage.InputTokens);
        Assert.AreEqual(21, usage.OutputTokens);
        Assert.AreEqual(5, usage.CachedTokens);
        Assert.AreEqual(34, usage.TotalTokens);
    }

    [TestMethod]
    public async Task TokenomicsProcessor_EmitsCohereFieldsForModelNormalization()
    {
        const string responseBody = """
            {
              "meta": {
                "billed_units": {
                  "input_tokens": 6,
                  "output_tokens": 9
                }
              }
            }
            """;

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage("command-r-plus", stats);

        Assert.AreEqual(UsageProvider.Cohere, usage.Provider);
        Assert.AreEqual(6, usage.InputTokens);
        Assert.AreEqual(9, usage.OutputTokens);
        Assert.AreEqual(15, usage.TotalTokens);
    }

    [TestMethod]
    public async Task MissingUsage_RemainsUnavailableInsteadOfBecomingMeasuredZeros()
    {
        const string responseBody = "{\"choices\":[]}";

        var (_, stats) = await ProcessAsync(responseBody);
        var usage = LLMHandler.PopulateUsage("gpt-4o", stats);

        Assert.AreEqual("False", stats["Usage.Available"]);
        Assert.IsFalse(usage.UsageAvailable);
        Assert.IsFalse(stats.ContainsKey("Usage.Prompt_Tokens"));
        Assert.IsFalse(stats.ContainsKey("Usage.Completion_Tokens"));
        Assert.IsFalse(stats.ContainsKey("Usage.Total_Tokens"));
    }

    private static async Task<(string Output, Dictionary<string, string> Stats)> ProcessAsync(string responseBody)
    {
        using var processor = new TokenomicsProcessor();
        return await ProcessAsync(processor, responseBody);
    }

    private static async Task<(string Output, Dictionary<string, string> Stats)> ProcessAsync(
        IStreamProcessor processor,
        string responseBody)
    {
        using var content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream");
        using var output = new MemoryStream();

        await processor.CopyToAsync(content, output, debug: false);

        var stats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var response = new HttpResponseMessage();
        processor.GetStats(stats, response.Headers);

        return (Encoding.UTF8.GetString(output.ToArray()), stats);
    }
}