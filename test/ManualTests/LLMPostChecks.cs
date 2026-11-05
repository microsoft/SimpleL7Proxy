#:project ../../src/Shared-parser/Shared-parser.csproj

using System.Reflection;
using System.Text;
using System.Text.Json;
using SharedParser.Tokenomics.LLM;

int checks = 0;
var formats = Enum.GetValues<LLMApiFormatEnum>();
string sampleRoot = Path.Combine(Directory.GetCurrentDirectory(), "src/Shared-parser/Tokenomics/LLM");
string[] sampleNames = ["completions", "responses", "anthropic-messages"];
{
    string[] sourceLines = File.ReadAllLines(Path.Combine(sampleRoot, "sample-completions.json"));
    using var original = JsonDocument.Parse(string.Join('\n', sourceLines));
    string model = original.RootElement.GetProperty("model").GetString()!;
    using var messagesStream = new MemoryStream();
    var toMessages = new LLMContentMapper(model, model, messagesStream,
        LLMApiFormatEnum.OpenAIChatCompletions, LLMApiFormatEnum.AnthropicMessages);
    foreach (string line in sourceLines) toMessages.ProcessLine(line);
    toMessages.Complete();
    string messagesJson = Encoding.UTF8.GetString(messagesStream.ToArray());
    using var messages = JsonDocument.Parse(messagesJson);
    Check(messages.RootElement.GetProperty("type").GetString() == "message", "round trip intermediate is Anthropic Messages");
    using var completionStream = new MemoryStream();
    var toCompletion = new LLMContentMapper(model, model, completionStream,
        LLMApiFormatEnum.AnthropicMessages, LLMApiFormatEnum.OpenAIChatCompletions);
    foreach (string line in messagesJson.Split('\n')) toCompletion.ProcessLine(line);
    toCompletion.Complete();
    string completionJson = Encoding.UTF8.GetString(completionStream.ToArray());
    using var completion = JsonDocument.Parse(completionJson);
    var result = completion.RootElement;
    var source = original.RootElement;
    Check(result.GetProperty("object").GetString() == "chat.completion", "round trip result is Chat Completions");
    Check(SourceText(completionJson.Split('\n'), LLMApiFormatEnum.OpenAIChatCompletions, false)
        == SourceText(sourceLines, LLMApiFormatEnum.OpenAIChatCompletions, false), "round trip preserves exact generated text");
    foreach (string property in new[] { "id", "model" })
        Check(JsonElement.DeepEquals(source.GetProperty(property), result.GetProperty(property)), $"round trip preserves {property}");
    var originalChoice = source.GetProperty("choices")[0];
    var resultChoice = result.GetProperty("choices")[0];
    Check(JsonElement.DeepEquals(originalChoice.GetProperty("finish_reason"), resultChoice.GetProperty("finish_reason")), "round trip preserves finish reason");
    Check(JsonElement.DeepEquals(originalChoice.GetProperty("message").GetProperty("role"), resultChoice.GetProperty("message").GetProperty("role")), "round trip preserves role");
    foreach (string property in new[] { "prompt_tokens", "completion_tokens", "total_tokens" })
        Check(JsonElement.DeepEquals(source.GetProperty("usage").GetProperty(property), result.GetProperty("usage").GetProperty(property)), $"round trip preserves usage.{property}");
    string[] omitted = source.EnumerateObject().Where(property => !result.TryGetProperty(property.Name, out _))
        .Select(property => property.Name).Concat(source.GetProperty("usage").EnumerateObject()
            .Where(property => !result.GetProperty("usage").TryGetProperty(property.Name, out _))
            .Select(property => "usage." + property.Name)).ToArray();
    Console.WriteLine("ROUND TRIP PASS: Chat Completions -> Anthropic Messages -> Chat Completions.");
    Console.WriteLine("Exact text, id, model, role, finish reason, prompt/completion/total tokens preserved.");
    if (omitted.Length > 0) Console.WriteLine("Not round-tripped: " + string.Join(", ", omitted));
}
foreach (bool streaming in new[] { false, true }) {
    for (int sourceIndex = 0; sourceIndex < formats.Length; sourceIndex++) {
        string[] lines = File.ReadAllLines(Path.Combine(sampleRoot, $"sample-{sampleNames[sourceIndex]}{(streaming ? "-sse" : "")}.json"));
        string expected = SourceText(lines, formats[sourceIndex], streaming);
        foreach (var target in formats) {
            using var stream = new MemoryStream();
            Action<string> process;
            Action complete;
            Func<LLMPostamble> metadata;
            object mapper;
            if (streaming) {
                var sseMapper = new LLMSSEContentMapper("source", "target", stream, formats[sourceIndex], target);
                mapper = sseMapper;
                process = sseMapper.ProcessLine;
                complete = sseMapper.Complete;
                metadata = () => sseMapper.Postamble;
            }
            else {
                var jsonMapper = new LLMContentMapper("source", "target", stream, formats[sourceIndex], target);
                mapper = jsonMapper;
                process = jsonMapper.ProcessLine;
                complete = jsonMapper.Complete;
                metadata = () => jsonMapper.Postamble;
            }
            foreach (string line in lines) {
                process(line);
            }
            complete();
            long length = stream.Length;
            complete();
            Check(stream.Length == length && stream.CanWrite, "completion is once-only and leaves the stream open");
            string output = Encoding.UTF8.GetString(stream.ToArray());
            Check(SourceText(output.Replace("\r\n", "\n").Split('\n'), target, streaming) == expected,
                $"text preserved: {formats[sourceIndex]} -> {target}, streaming={streaming}");
            var postamble = metadata();
            Check(postamble.OutputTokens is not null, "source usage captured");
            var roots = streaming ? Events(output) : [JsonDocument.Parse(output).RootElement.Clone()];
            JsonElement final = streaming && target == LLMApiFormatEnum.OpenAIResponses
                ? roots.Last().GetProperty("response") : roots.Last();
            if (target == LLMApiFormatEnum.OpenAIChatCompletions) {
                JsonElement usageRoot = roots.Last(root => root.TryGetProperty("usage", out _));
                Check(usageRoot.GetProperty("usage").GetProperty("completion_tokens").GetInt64() == postamble.OutputTokens, "Chat usage serialized");
                JsonElement finish = roots.Last(root => root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0);
                Check(finish.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == (postamble.FinishReason ?? "stop"), "Chat finish reason serialized");
                if (streaming) Check(output.EndsWith("data: [DONE]\n\n"), "Chat terminal marker last");
            }
            else if (target == LLMApiFormatEnum.OpenAIResponses) {
                string status = postamble.Status ?? (postamble.FinishReason is "length" or "content_filter" ? "incomplete" : "completed");
                Check(final.GetProperty("status").GetString() == status, "Responses terminal status serialized");
                Check(final.GetProperty("usage").GetProperty("output_tokens").GetInt64() == postamble.OutputTokens, "Responses usage serialized");
                if (streaming) {
                    Check(roots.Last().GetProperty("type").GetString() == $"response.{status}", "Responses terminal event last");
                    Check(roots.Single(root => root.GetProperty("type").GetString() == "response.output_text.done").GetProperty("text").GetString() == expected, "text.done repeats full text");
                    Check(roots.Single(root => root.GetProperty("type").GetString() == "response.content_part.done").GetProperty("part").GetProperty("text").GetString() == expected, "part.done repeats full text");
                    Check(roots.Single(root => root.GetProperty("type").GetString() == "response.output_item.done").GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString() == expected, "item.done repeats full text");
                    Check(final.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString() == expected, "terminal response repeats full text");
                    Check(roots.Select(root => root.GetProperty("sequence_number").GetInt32()).SequenceEqual(Enumerable.Range(0, roots.Length)), "sequence numbers monotonic");
                    object formatter = mapper.GetType().GetField("_dataWriter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mapper)!;
                    var buffer = (StringBuilder)formatter.GetType().GetField("_content", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(formatter)!;
                    Check(buffer.Length == 0 && buffer.Capacity == 0, "Responses retained text released");
                }
            }
            else {
                JsonElement delta = streaming ? roots.Single(root => root.GetProperty("type").GetString() == "message_delta") : final;
                Check(delta.GetProperty("usage").GetProperty("output_tokens").GetInt64() == postamble.OutputTokens, "Anthropic usage serialized");
                Check((streaming ? delta.GetProperty("delta") : delta).GetProperty("stop_reason").GetString() == (postamble.FinishReason == "length" ? "max_tokens" : "end_turn"), "Anthropic stop reason mapped");
                if (streaming) Check(roots.Last().GetProperty("type").GetString() == "message_stop", "Anthropic terminal event last");
            }
        }
    }
}

foreach (var target in formats) {
    using var stream = new MemoryStream();
    var mapper = new LLMSSEContentMapper("source", "target", stream, LLMApiFormatEnum.OpenAIChatCompletions, target);
    mapper.ProcessLine("data: {\"choices\":[{\"finish_reason\":\"length\",\"delta\":{\"content\":\"last\"}}]}");
    mapper.ProcessLine("");
    string before = Encoding.UTF8.GetString(stream.ToArray());
    Check(!before.Contains("response.output_text.done") && !before.Contains("content_block_stop") && !before.Contains("[DONE]"), "finish reason does not finalize early");
    mapper.ProcessLine("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_tokens_details\":{\"cached_tokens\":3}}}");
    mapper.ProcessLine("");
    mapper.ProcessLine("data: [DONE]");
    mapper.Complete();
    Check(mapper.Postamble.CachedInputTokens == 3 && mapper.Postamble.InputTokens == 10, "late usage captured after finish reason");
    Check(SourceText(Encoding.UTF8.GetString(stream.ToArray()).Split('\n'), target, true) == "last", "same-chunk content follows finish reason");
}

foreach (string terminal in new[] { "response.completed", "response.incomplete" }) {
    using var stream = new MemoryStream();
    var mapper = new LLMSSEContentMapper("source", "target", stream, LLMApiFormatEnum.OpenAIResponses, LLMApiFormatEnum.OpenAIResponses);
    mapper.ProcessLine("data: {\"type\":\"response.created\",\"response\":{\"status\":\"in_progress\"}}");
    mapper.ProcessLine("");
    mapper.ProcessLine("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}");
    mapper.ProcessLine("");
    mapper.ProcessLine($"event: {terminal}");
    Check(!Encoding.UTF8.GetString(stream.ToArray()).Contains("response.output_text.done"), "event header alone does not finalize");
    mapper.ProcessLine($"data: {{\"type\":\"{terminal}\",\"response\":{{\"usage\":{{\"input_tokens\":1,\"output_tokens\":2}}}}}}");
    mapper.Complete();
    Check(Events(Encoding.UTF8.GetString(stream.ToArray())).Last().GetProperty("type").GetString() == terminal, "EOF finalizes parsed terminal payload");
}

foreach (var source in formats) {
    foreach (var target in formats) {
        foreach (bool afterText in new[] { false, true }) {
            foreach (string failure in new[] {
                "event: response.failed", "event: error",
                "data: {\"type\":\"response.failed\"}",
                "data: {\"type\":\"error\",\"error\":{\"message\":\"backend failed\"}}",
                "data: {\"response\":{\"status\":\"failed\"}}",
                "data: {\"error\":{\"message\":\"backend failed\"}}",
                "data: {\"error\":{}}"
            }) {
                using var stream = new MemoryStream();
                var mapper = new LLMSSEContentMapper("source", "target", stream, source, target);
                if (afterText) {
                    mapper.ProcessLine(source switch {
                        LLMApiFormatEnum.OpenAIChatCompletions => "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}",
                        LLMApiFormatEnum.OpenAIResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
                        _ => "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}"
                    });
                    mapper.ProcessLine("");
                    Check(stream.Length > 0, "failure test has already emitted text");
                }
                long priorLength = stream.Length;
                bool threw = false;
                try { mapper.ProcessLine(failure); }
                catch (IOException) { threw = true; }
                Check(threw && mapper.Postamble.Status == "failed", $"{source} -> {target}: failure throws IOException");
                threw = false;
                try { mapper.Complete(); }
                catch (IOException) { threw = true; }
                Check(threw, "Complete cannot turn a failed stream into success");
                threw = false;
                try { mapper.ProcessLine("data: [DONE]"); }
                catch (IOException) { threw = true; }
                Check(threw, "later input cannot turn a failed stream into success");
                Check(stream.Length == priorLength && stream.CanWrite, "failure writes no footer and leaves the caller stream open");
            }
        }
    }
}

foreach (var target in formats) {
    using var stream = new MemoryStream();
    var mapper = new LLMSSEContentMapper("source", "target", stream, LLMApiFormatEnum.OpenAIChatCompletions, target);
    mapper.ProcessLine("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}");
    mapper.ProcessLine("");
    bool threw = false;
    try { mapper.ProcessLine("data: {\"choices\":[{\"delta\":{\"content\":\" partial\"}}],\"error\":{}}"); }
    catch (IOException) { threw = true; }
    Check(threw, "error after content in the same payload throws");
    long failedLength = stream.Length;
    threw = false;
    try { mapper.Complete(); }
    catch (IOException) { threw = true; }
    Check(threw && stream.Length == failedLength, "Complete does not replay a partially consumed failed payload");
}

foreach (var source in formats) {
    using var stream = new MemoryStream();
    var mapper = new LLMSSEContentMapper("source", "target", stream, source, LLMApiFormatEnum.OpenAIResponses);
    string line = source switch {
        LLMApiFormatEnum.OpenAIChatCompletions => "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}",
        LLMApiFormatEnum.OpenAIResponses => "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}",
        _ => "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hello\"}}"
    };
    mapper.ProcessLine(line);
    mapper.ProcessLine("");
    ThrowsEof(mapper.Complete);
    Check(!Encoding.UTF8.GetString(stream.ToArray()).Contains("response.output_text.done"), "missing terminal leaves footer unwritten");
}
using (var stream = new MemoryStream()) {
    var mapper = new LLMContentMapper("source", "target", stream, LLMApiFormatEnum.OpenAIChatCompletions, LLMApiFormatEnum.OpenAIResponses);
    mapper.ProcessLine("{\"choices\":[{\"message\":{\"content\":\"hello\"}}],\"usage\":{");
    ThrowsEof(mapper.Complete);
}
using (var stream = new MemoryStream()) {
    var mapper = new LLMSSEContentMapper("source", "target", stream, LLMApiFormatEnum.OpenAIResponses, LLMApiFormatEnum.OpenAIResponses);
    mapper.ProcessLine("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}");
    mapper.ProcessLine("");
    mapper.ProcessLine("event: response.completed");
    mapper.ProcessLine("data: {\"type\":\"response.completed\",\"response\":{");
    ThrowsEof(mapper.Complete);
}
Console.WriteLine($"SUCCESS: {checks} LLM Post checks passed.");

void Check(bool condition, string description) {
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

void ThrowsEof(Action complete) {
    try { complete(); }
    catch (EndOfStreamException) { checks++; return; }
    throw new InvalidOperationException("Truncated source was accepted.");
}

JsonElement[] Events(string output) => output.Replace("\r\n", "\n").Split('\n')
    .Where(line => line.StartsWith("data: ") && line != "data: [DONE]")
    .Select(line => JsonDocument.Parse(line[6..]).RootElement.Clone()).ToArray();

string SourceText(string[] lines, LLMApiFormatEnum format, bool streaming) {
    if (!streaming) {
        using var document = JsonDocument.Parse(string.Join('\n', lines));
        var root = document.RootElement;
        return format switch {
            LLMApiFormatEnum.OpenAIChatCompletions => string.Concat(root.GetProperty("choices").EnumerateArray().Select(choice => choice.GetProperty("message").GetProperty("content").GetString())),
            LLMApiFormatEnum.OpenAIResponses => string.Concat(root.GetProperty("output").EnumerateArray().Where(item => item.GetProperty("type").GetString() == "message").SelectMany(item => item.GetProperty("content").EnumerateArray()).Where(part => part.GetProperty("type").GetString() == "output_text").Select(part => part.GetProperty("text").GetString())),
            _ => string.Concat(root.GetProperty("content").EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()))
        };
    }
    var text = new StringBuilder();
    foreach (var root in Events(string.Join('\n', lines))) {
        if (format == LLMApiFormatEnum.OpenAIChatCompletions && root.TryGetProperty("choices", out var choices)) {
            foreach (var choice in choices.EnumerateArray()) {
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)) text.Append(content.GetString());
            }
        }
        else if (format == LLMApiFormatEnum.OpenAIResponses && root.GetProperty("type").GetString() == "response.output_text.delta") text.Append(root.GetProperty("delta").GetString());
        else if (format == LLMApiFormatEnum.AnthropicMessages && root.GetProperty("type").GetString() == "content_block_delta"
            && root.GetProperty("delta").TryGetProperty("text", out var content)) text.Append(content.GetString());
    }
    return text.ToString();
}