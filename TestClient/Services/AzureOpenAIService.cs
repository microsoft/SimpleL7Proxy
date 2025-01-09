using OpenAI.Chat;
using System.ClientModel;

namespace TestClient.Services
{
    public class AzureOpenAIService(ChatClient chatClient)
    {
        public void StreamOutCompletion(Action<string> streamingOutput, params ChatMessage[] chatMessages)
        {
            CollectionResult<StreamingChatCompletionUpdate> completionUpdates = chatClient.CompleteChatStreaming(chatMessages);

            foreach (StreamingChatCompletionUpdate completionUpdate in completionUpdates)
            {
                foreach (ChatMessageContentPart contentPart in completionUpdate.ContentUpdate)
                {
                    streamingOutput(contentPart.Text);
                }
            }
        }

        public ChatCompletion ChatCompletion(params ChatMessage[] chatMessages)
            => chatClient.CompleteChat(chatMessages);
    }
}
