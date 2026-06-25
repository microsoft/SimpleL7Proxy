using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace chat_tester.Components.Shared;

/// <summary>
/// Rewrites a single-turn model request so it carries the current multi-turn chat history.
/// </summary>
public static class MultiStepChatRequestBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Build(string requestBody, IReadOnlyList<MultiStepChatTurn> turns, string currentUserMessage)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return requestBody;
        }

        var root = JsonNode.Parse(requestBody)?.AsObject();
        if (root is null)
        {
            return requestBody;
        }

        if (root.ContainsKey("messages"))
        {
            root["messages"] = BuildMessages(turns, currentUserMessage);
        }
        else if (root.ContainsKey("contents"))
        {
            root["contents"] = BuildGeminiContents(turns, currentUserMessage);
        }
        else if (root.ContainsKey("message"))
        {
            root["chat_history"] = BuildCohereHistory(turns);
            root["message"] = currentUserMessage;
        }
        else if (root.ContainsKey("input"))
        {
            root["input"] = BuildResponsesInput(turns, currentUserMessage);
        }
        else if (root.ContainsKey("prompt"))
        {
            root["prompt"] = BuildPromptTranscript(turns, currentUserMessage);
        }

        return root.ToJsonString(SerializerOptions);
    }

    private static JsonArray BuildMessages(IReadOnlyList<MultiStepChatTurn> turns, string currentUserMessage)
    {
        var messages = new JsonArray();
        foreach (var turn in turns)
        {
            AddMessage(messages, "user", turn.UserMessage);
            AddMessage(messages, "assistant", turn.AssistantMessage);
        }

        AddMessage(messages, "user", currentUserMessage);
        return messages;
    }

    private static JsonArray BuildResponsesInput(IReadOnlyList<MultiStepChatTurn> turns, string currentUserMessage)
    {
        var input = new JsonArray();
        foreach (var turn in turns)
        {
            AddMessage(input, "user", turn.UserMessage);
            AddMessage(input, "assistant", turn.AssistantMessage);
        }

        AddMessage(input, "user", currentUserMessage);
        return input;
    }

    private static JsonArray BuildGeminiContents(IReadOnlyList<MultiStepChatTurn> turns, string currentUserMessage)
    {
        var contents = new JsonArray();
        foreach (var turn in turns)
        {
            AddGeminiContent(contents, "user", turn.UserMessage);
            AddGeminiContent(contents, "model", turn.AssistantMessage);
        }

        AddGeminiContent(contents, "user", currentUserMessage);
        return contents;
    }

    private static JsonArray BuildCohereHistory(IReadOnlyList<MultiStepChatTurn> turns)
    {
        var history = new JsonArray();
        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.UserMessage))
            {
                history.Add(new JsonObject
                {
                    ["role"] = "USER",
                    ["message"] = turn.UserMessage
                });
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantMessage))
            {
                history.Add(new JsonObject
                {
                    ["role"] = "CHATBOT",
                    ["message"] = turn.AssistantMessage
                });
            }
        }

        return history;
    }

    private static string BuildPromptTranscript(IReadOnlyList<MultiStepChatTurn> turns, string currentUserMessage)
    {
        var lines = new List<string>();
        foreach (var turn in turns)
        {
            if (!string.IsNullOrWhiteSpace(turn.UserMessage))
            {
                lines.Add($"User: {turn.UserMessage}");
            }

            if (!string.IsNullOrWhiteSpace(turn.AssistantMessage))
            {
                lines.Add($"Assistant: {turn.AssistantMessage}");
            }
        }

        lines.Add($"User: {currentUserMessage}");
        lines.Add("Assistant:");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddMessage(JsonArray messages, string role, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        messages.Add(new JsonObject
        {
            ["role"] = role,
            ["content"] = content
        });
    }

    private static void AddGeminiContent(JsonArray contents, string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        contents.Add(new JsonObject
        {
            ["role"] = role,
            ["parts"] = new JsonArray
            {
                new JsonObject
                {
                    ["text"] = text
                }
            }
        });
    }
}

public sealed record MultiStepChatTurn(
    string UserMessage,
    string AssistantMessage,
    DateTimeOffset CreatedAt,
    string Status,
    MultiStepChatMetrics Metrics,
    string RequestHeadersText,
    string ResponseHeadersText,
    string RawResponseText);

public sealed class MultiStepChatMetrics
{
    public string Status { get; set; } = "-";
    public string ContentType { get; set; } = "-";
    public TimeSpan? TimeToFirstByte { get; set; }
    public TimeSpan? Duration { get; set; }
    public int Chunks { get; set; }
    public long TotalBytes { get; set; }
}