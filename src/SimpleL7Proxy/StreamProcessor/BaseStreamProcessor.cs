using System.Net.Http.Headers;
using SimpleL7Proxy.Events;

/*
 * STREAM PROCESSOR BASE CLASS DOCUMENTATION
 * =======================================
 * 
 * PURPOSE:
 * - Provides common disposal pattern for all stream processors
 * - Implements standard IDisposable pattern with _disposed flag
 * - Provides template methods for extensibility
 * - Reduces code duplication across processor implementations
 * 
 * DISPOSAL PATTERN:
 * - Consistent _disposed field and Dispose pattern across all processors
 * - Protected virtual Dispose(bool disposing) for derived class customization
 * - GC.SuppressFinalize(this) called in public Dispose method
 * 
 * TEMPLATE METHOD PATTERN:
 * - Abstract methods that derived classes must implement
 * - Virtual methods that derived classes can optionally override
 * - Common infrastructure handled by base class
 * 
 * DERIVED CLASS REQUIREMENTS:
 * - Must implement CopyToAsync for actual stream processing logic
 * - Must implement GetStats for statistics extraction
 * - Can override Dispose(bool disposing) to clean up specific resources
 * - Should call base.Dispose(disposing) if overriding disposal
 */

namespace SimpleL7Proxy.StreamProcessor
{
    /// <summary>
    /// Abstract base class for stream processors that provides common disposal pattern
    /// and template methods for stream processing operations.
    /// </summary>
    public abstract class BaseStreamProcessor : IStreamProcessor
    {
        protected bool _disposed = false;

        /// <summary>
        /// Abstract method that derived classes must implement to handle stream copying logic.
        /// </summary>
        /// <param name="sourceContent">The source HTTP content to read from.</param>
        /// <param name="outputStream">The destination stream to write to.</param>
        /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous copy operation.</returns>
        public abstract Task CopyToAsync(System.Net.Http.HttpContent sourceContent, Stream outputStream);

        /// <summary>
        /// Abstract method that derived classes must implement to extract and populate statistics.
        /// </summary>
        /// <param name="eventData">The event data object to populate with statistics.</param>
        /// <param name="headers">The HTTP response headers containing potential statistics.</param>
        public abstract void GetStats(ProxyEvent eventData, HttpResponseHeaders headers);

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern. Derived classes can override to clean up specific resources.
        /// </summary>
        /// <param name="disposing">True if disposing managed resources, false if called from finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Base class has no managed resources to clean up
                    // Derived classes can override to clean up their specific resources
                }
                _disposed = true;
            }
        }

        /// <summary>
        /// Helper method to check if the processor has been disposed.
        /// Derived classes can use this to validate state before operations.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
