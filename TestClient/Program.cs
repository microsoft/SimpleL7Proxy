using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using System.ClientModel;
using System.Reflection;
using TestClient;

ApplicationSettings applicationSettings = GetApplicationSettings();
Uri azureOpenAIEndpoint = new (applicationSettings.AzureOpenAI.Endpoint);
DefaultAzureCredential credentials = new();
ApiKeyCredential keyCredentials = new(applicationSettings.AzureOpenAI.Key);
AzureOpenAIClient azureClient = new(azureOpenAIEndpoint, keyCredentials);
ChatClient chatClient = azureClient.GetChatClient(applicationSettings.AzureOpenAI.DeploymentName);

Console.WriteLine("CALLING STREAMING");
CollectionResult<StreamingChatCompletionUpdate> completionUpdates = chatClient.CompleteChatStreaming(
    [
        new SystemChatMessage("You are a helpful assistant that talks like a pirate."),
        new UserChatMessage("Hi, can you help me?"),
        new AssistantChatMessage("Arrr! Of course, me hearty! What can I do for ye?"),
        new UserChatMessage("Write me a 2500 word paper on how to train a parrot to talk and be a friend. Be as proffessional and technical as possible and evaluate your response. Be long winded an imaginative."),
    ]);

foreach (StreamingChatCompletionUpdate completionUpdate in completionUpdates)
{
    foreach (ChatMessageContentPart contentPart in completionUpdate.ContentUpdate)
    {
        Console.Write(contentPart.Text);
    }
}
Console.WriteLine($"{Environment.NewLine}DONE WITH STREAMING{Environment.NewLine}{Environment.NewLine}");
Console.WriteLine($"CALLING COMPLETIONS{Environment.NewLine}");

ChatCompletion completion = chatClient.CompleteChat(
    [
        new SystemChatMessage("You are a helpful assistant that talks like a pirate."),
        new UserChatMessage("Hi, can you help me?"),
        new AssistantChatMessage("Arrr! Of course, me hearty! What can I do for ye?"),
        new UserChatMessage("Write me a 500 word paper on how to train a parrot to talk and be a friend."),
    ]);

Console.WriteLine(completion.Content[0].Text);

Console.WriteLine($"{Environment.NewLine}DONE WITH COMPLETION");
Console.ReadLine();

static ApplicationSettings GetApplicationSettings()
{
    IConfigurationRoot config = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .AddUserSecrets(Assembly.GetExecutingAssembly())
        .Build();

    return config.GetSection("ApplicationSettings").Get<ApplicationSettings>();
}