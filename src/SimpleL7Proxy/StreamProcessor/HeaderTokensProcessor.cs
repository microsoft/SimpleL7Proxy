using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.StreamProcessor
{
    public class HeaderTokensProcessor : IStreamProcessor
    {
        public Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream, CancellationToken? cancellationToken)
        {
            // Do nothing
            return Task.CompletedTask;
        }

        public void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            //eventData["CompletionTokens"] = headers.GetValues("X-Completion-Tokens").FirstOrDefault() ?? "0";

        }
    }
}