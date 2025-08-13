using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Null object pattern implementation of IStreamProcessor that performs no operations.
    /// Useful for testing or when no stream processing is needed.
    /// </summary>
    public class NullStreamProcessor : BaseStreamProcessor
    {
        /// <summary>
        /// Does nothing - null object pattern implementation.
        /// </summary>
        public override Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream, CancellationToken? cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Does nothing - null object pattern implementation.
        /// </summary>
        public override void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // No stats to collect in a null processor
        }

        // No need to override Dispose - base class handles it
    }
}