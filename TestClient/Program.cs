using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using System.ClientModel;

var azureOpenAIDeploymentName = "gpt-4o-eastus";
var apiKey = "";
Uri azureOpenAIEndpoint = new ("http://localhost:7002/");
DefaultAzureCredential credentials = new();
ApiKeyCredential keyCredentials = new("");
AzureOpenAIClient azureClient = new(azureOpenAIEndpoint, keyCredentials);
ChatClient chatClient = azureClient.GetChatClient(azureOpenAIDeploymentName);
CollectionResult<StreamingChatCompletionUpdate> completionUpdates = chatClient.CompleteChatStreaming(
    [
        new SystemChatMessage("You are a helpful assistant that talks like a pirate."),
        new UserChatMessage("Hi, can you help me?"),
        new AssistantChatMessage("Arrr! Of course, me hearty! What can I do for ye?"),
        new UserChatMessage("What's the best way to train a parrot?"),
    ]);

foreach (StreamingChatCompletionUpdate completionUpdate in completionUpdates)
{
    foreach (ChatMessageContentPart contentPart in completionUpdate.ContentUpdate)
    {
        Console.Write(contentPart.Text);
    }
}

Console.WriteLine("DONE");
Console.ReadLine();