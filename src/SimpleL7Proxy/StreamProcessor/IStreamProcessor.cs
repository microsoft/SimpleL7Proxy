namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Interface for processing streams with source and destination stream support.
    /// </summary>
    public interface IStreamProcessor
    {
        /// <summary>
        /// Copies data from the source stream to the destination output stream asynchronously.
        /// </summary>
        /// <param name="sourceStream">The source stream to read from.</param>
        /// <param name="outputStream">The destination stream to write to.</param>
        /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous copy operation.</returns>
        Task CopyToAsync(System.Net.Http.HttpContent sourceStream, Stream outputStream, CancellationToken? cancellationToken);
        
        /// <summary>
        /// Gets statistics about the stream processing operation.
        /// </summary>
        /// <returns>A dictionary containing processing statistics.</returns>
        Dictionary<string, string> GetStats();
    }
}