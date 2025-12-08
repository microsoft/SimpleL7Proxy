using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.BlobStorage
{
    /// <summary>
    /// A transparent stream that captures writes and queues them for batched blob storage.
    /// Wraps the underlying blob stream but defers actual writes to a background queue.
    /// </summary>
    internal class QueuedBlobStream : Stream
    {
        private readonly MemoryStream _buffer;
        private readonly BlobWriteQueue _queue;
        private readonly string _containerName;
        private readonly string _blobName;
        private readonly ILogger _logger;
        private bool _disposed;

        public QueuedBlobStream(
            BlobWriteQueue queue,
            string containerName,
            string blobName,
            ILogger logger)
        {
            _buffer = new MemoryStream();
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _containerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            _blobName = blobName ?? throw new ArgumentNullException(nameof(blobName));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Synchronous flush - just ensure buffer is flushed
            _buffer.Flush();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_disposed || _buffer.Length == 0)
                return;

            // Queue the buffered data for writing
            var data = _buffer.ToArray();
            var operation = new BlobWriteOperation
            {
                ContainerName = _containerName,
                BlobName = _blobName,
                Data = new ReadOnlyMemory<byte>(data),
                Priority = 0
            };

            await _queue.EnqueueAsync(operation, cancellationToken).ConfigureAwait(false);
            
            _logger.LogTrace(
                "[QueuedBlobStream] Enqueued {Size}B for {Container}/{Blob}",
                data.Length, _containerName, _blobName);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QueuedBlobStream));

            _buffer.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QueuedBlobStream));

            await _buffer.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QueuedBlobStream));

            await _buffer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("QueuedBlobStream does not support reading.");

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("QueuedBlobStream does not support seeking.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("QueuedBlobStream does not support SetLength.");

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Synchronously flush on dispose - this will queue the operation
                // The caller should have called FlushAsync before disposing ideally
                if (_buffer.Length > 0)
                {
                    FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                _buffer.Dispose();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            // Async flush before disposing
            if (_buffer.Length > 0)
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await _buffer.DisposeAsync().ConfigureAwait(false);
            _disposed = true;

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decorator for IBlobWriter that transparently queues write operations through BlobWriteQueue.
    /// Reads and metadata operations are passed through directly to the underlying writer.
    /// </summary>
    public class QueuedBlobWriter : IBlobWriter
    {
        private readonly IBlobWriter _underlyingWriter;
        private readonly BlobWriteQueue _queue;
        private readonly ILogger<QueuedBlobWriter> _logger;
        private readonly bool _useQueueForWrites;

        public QueuedBlobWriter(
            IBlobWriter underlyingWriter,
            BlobWriteQueue queue,
            ILogger<QueuedBlobWriter> logger,
            bool useQueueForWrites = true)
        {
            _underlyingWriter = underlyingWriter ?? throw new ArgumentNullException(nameof(underlyingWriter));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _useQueueForWrites = useQueueForWrites;

            _logger.LogInformation(
                "[QueuedBlobWriter] Initialized - QueuedWrites: {UseQueue}, Underlying: {UnderlyingType}",
                _useQueueForWrites, _underlyingWriter.GetType().Name);
        }

        /// <summary>
        /// Creates a blob output stream. If queuing is enabled, returns a QueuedBlobStream
        /// that buffers writes and enqueues them. Otherwise, returns the direct stream.
        /// </summary>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string userId, string blobName)
        {
            if (_useQueueForWrites)
            {
                // Ensure container is initialized first
                await _underlyingWriter.InitClientAsync(userId, userId).ConfigureAwait(false);

                // Return a queued stream that buffers and enqueues writes
                _logger.LogTrace(
                    "[QueuedBlobWriter] Creating queued stream for {Container}/{Blob}",
                    userId, blobName);

                return new QueuedBlobStream(_queue, userId, blobName, _logger);
            }
            else
            {
                // Pass through to underlying writer
                return await _underlyingWriter.CreateBlobAndGetOutputStreamAsync(userId, blobName)
                    .ConfigureAwait(false);
            }
        }

        // Pass-through methods - these don't benefit from queuing

        public Task<bool> BlobExistsAsync(string userId, string blobName) =>
            _underlyingWriter.BlobExistsAsync(userId, blobName);

        public Task<Stream> ReadBlobAsStreamAsync(string userId, string blobName) =>
            _underlyingWriter.ReadBlobAsStreamAsync(userId, blobName);

        public Task<bool> DeleteBlobAsync(string userId, string blobName) =>
            _underlyingWriter.DeleteBlobAsync(userId, blobName);

        public Task<string> GenerateSasTokenAsync(string userId, string blobName, TimeSpan expiryTime) =>
            _underlyingWriter.GenerateSasTokenAsync(userId, blobName, expiryTime);

        public string GetBlobUri(string userId, string blobName) =>
            _underlyingWriter.GetBlobUri(userId, blobName);

        public Task<bool> InitClientAsync(string userId, string containerName) =>
            _underlyingWriter.InitClientAsync(userId, containerName);

        public bool IsInitialized => _underlyingWriter.IsInitialized;

        public string GetConnectionInfo() =>
            _underlyingWriter.GetConnectionInfo() + " (Queued)";
    }
}
