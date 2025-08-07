namespace SimpleL7Proxy.StreamProcessor
{
    public class NullStreamProcessor : IStreamProcessor
    {
        public Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream, CancellationToken? cancellationToken)
        {
            // Do nothing
            return Task.CompletedTask;
        }

        public Dictionary<string, string> GetStats()
        {
            return new();
        }
    }
}