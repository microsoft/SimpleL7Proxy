using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Default stream processor implementation that simply copies content without processing.
    /// Suitable for APIs that don't require specific JSON parsing or statistics extraction.
    /// </summary>
    public class DefaultStreamProcessor : BaseStreamProcessor
    {
        /// <summary>
        /// Copies content from source to destination without any processing.
        /// </summary>
        public override async Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream)
        {
            //            if (cancellationToken != null)
            //              await sourceContent.CopyToAsync(outputStream, cancellationToken.Value).ConfigureAwait(false);
            //        else
            await sourceContent.CopyToAsync(outputStream).ConfigureAwait(false);
        }

        /// <summary>
        /// No statistics are extracted by the default processor.
        /// </summary>
        public override void GetStats(ProxyEvent eventData, HttpResponseHeaders headers)
        {
            // No stats to collect in default processor
        }
    }
}