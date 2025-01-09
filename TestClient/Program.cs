using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using System.ClientModel;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestClient;
using TestClient.Services;

namespace TestClient
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            ApplicationSettings applicationSettings = GetApplicationSettings();
            Uri azureOpenAIEndpoint = new(applicationSettings.AzureOpenAI.Endpoint);
            DefaultAzureCredential credentials = new();
            ApiKeyCredential keyCredentials = new(applicationSettings.AzureOpenAI.Key);
            AzureOpenAIClient azureClient = new(azureOpenAIEndpoint, keyCredentials);
            ChatClient chatClient = azureClient.GetChatClient(applicationSettings.AzureOpenAI.DeploymentName);
            AzureOpenAIClient authAzureClient = new(azureOpenAIEndpoint, new DefaultAzureCredential());
            ChatClient authChatClient = authAzureClient.GetChatClient(applicationSettings.AzureOpenAI.DeploymentName);

            Console.WriteLine("CALLING STREAMING");
            ExecuteStreaming(chatClient);
            Console.WriteLine($"{Environment.NewLine}DONE WITH STREAMING{Environment.NewLine}{Environment.NewLine}");

            Console.WriteLine($"CALLING COMPLETIONS{Environment.NewLine}");
            ExecuteCompletion(chatClient);
            Console.WriteLine($"{Environment.NewLine}DONE WITH COMPLETION");

            Console.WriteLine($"{Environment.NewLine}CALLING ECHO ENDPOINT{Environment.NewLine}{Environment.NewLine}");
            await ExecuteBasicPost(applicationSettings);
            Console.WriteLine($"{Environment.NewLine}DONE WITH ECHO ENDPOINT");

            Console.ReadLine();
        }

        static void ExecuteStreaming(ChatClient chatClient)
        {
            AzureOpenAIService azureOpenAIService = new(chatClient);
            ChatMessage[] chatMessages = [
                new SystemChatMessage("You are a helpful assistant that talks like a pirate."),
                new UserChatMessage("Hi, can you help me?"),
                new AssistantChatMessage("Arrr! Of course, me hearty! What can I do for ye?"),
                new UserChatMessage("Write me a 2500 word paper on how to train a parrot to talk and be a friend. Be as proffessional and technical as possible and evaluate your response. Be long winded an imaginative."),
            ];
            
            azureOpenAIService.StreamOutCompletion(Console.Write, chatMessages);
        }

        static void ExecuteCompletion(ChatClient chatClient)
        {
            AzureOpenAIService azureOpenAIService = new(chatClient);
            ChatMessage[] chatMessages = [
                new SystemChatMessage("You are a helpful assistant that talks like a pirate."),
                new UserChatMessage("Hi, can you help me?"),
                new AssistantChatMessage("Arrr! Of course, me hearty! What can I do for ye?"),
                new UserChatMessage("Write me a 500 word paper on how to train a parrot to talk and be a friend. Be as proffessional and technical as possible and evaluate your response. Be long winded an imaginative."),
            ];
            ChatCompletion completion = azureOpenAIService.ChatCompletion(chatMessages);

            Console.WriteLine(completion.Content[0].Text);
        }

        static async Task ExecuteBasicPost(ApplicationSettings applicationSettings)
        {
            using (HttpClient client = new HttpClient())
            {
                Resource data = new("train", "125", "90", "mph");
                EchoAPIService echoAPIService = new(client);
                string resource = await echoAPIService.ResourceChange(
                    data,
                    applicationSettings.ClientSettings.SubscriptionKey,
                    applicationSettings.ClientSettings.Endpoint
                );

                Console.WriteLine(resource);
            }
        }

        static ApplicationSettings GetApplicationSettings()
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddUserSecrets(Assembly.GetExecutingAssembly())
                .Build();

            return config.GetSection("ApplicationSettings").Get<ApplicationSettings>();
        }
    }
}

