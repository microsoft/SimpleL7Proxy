// Uncomment to test blob shutdown behavior with 100 copies per write
//#define TEST_BLOB_SHUTDOWN

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// A transparent stream that captures writes and queues them for batched blob storage.
    /// Wraps the underlying blob stream but defers actual writes to a background queue.
    /// </summary>
    internal class QueuedBlobStream : Stream
    {
        private readonly MemoryStream _buffer;
        private readonly BlobWorkerPump _queue;
        private readonly string _containerName;
        private readonly string _blobName;
        private readonly ILogger _logger;
        private bool _disposed;
        private readonly List<Task<BlobWriteResult>> _pendingWrites = new();

        public QueuedBlobStream(
            BlobWorkerPump queue,
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

        // Stream.Flush is abstract; this is a no-op because writes go through FlushAsync
        // which is the path that actually enqueues the buffered data.
        public override void Flush() { }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            HarvestCompletedPendingWrites();

            if (_disposed || _buffer.Length == 0)
                return;

            // Queue the buffered data for writing
            var data = _buffer.ToArray();
            
            // Clear the buffer BEFORE enqueueing to prevent duplicate writes
            // if FlushAsync is called multiple times
            _buffer.SetLength(0);
            _buffer.Position = 0;
            

            var operation = new BlobWriteOperation
            {
                ContainerName = _containerName,
                BlobName = _blobName,
                Data = new ReadOnlyMemory<byte>(data),
            };

            await _queue.EnqueueAsync(operation, cancellationToken).ConfigureAwait(false);
            _pendingWrites.Add(operation.GetResultAsync());
            
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

        /// <summary>
        /// Waits for all enqueued blob write operations to complete.
        /// Call this before sending a "Completed" status so the client
        /// does not try to read the blob before it exists.
        /// </summary>
        public async Task WaitForPendingWritesAsync(CancellationToken cancellationToken = default)
        {
            HarvestCompletedPendingWrites();

            if (_pendingWrites.Count == 0)
                return;

            _logger.LogDebug(
                "[QueuedBlobStream] Waiting for {Count} pending writes for {Container}/{Blob}",
                _pendingWrites.Count, _containerName, _blobName);

            await Task.WhenAll(_pendingWrites).WaitAsync(cancellationToken).ConfigureAwait(false);
            _pendingWrites.Clear();
        }

        private void HarvestCompletedPendingWrites()
        {
            for (int i = _pendingWrites.Count - 1; i >= 0; i--)
            {
                if (_pendingWrites[i].IsCompletedSuccessfully)
                {
                    _pendingWrites.RemoveAt(i);
                }
            }
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
                // Sync Dispose path: do NOT flush here. Flushing would block on
                // _queue.EnqueueAsync via sync-over-async, risking thread-pool
                // starvation under load. All production callers go through
                // DisposeAsync (await using). Any data still in the buffer at
                // this point is dropped — fail-fast for the misuse case.
                if (_buffer.Length > 0)
                {
                    _logger.LogWarning(
                        "[QueuedBlobStream] Sync Dispose called with {Bytes}B unflushed for {Container}/{Blob}; data discarded — use await using instead.",
                        _buffer.Length, _containerName, _blobName);
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
        private readonly BlobWorkerPump _queue;
        private readonly ILogger<QueuedBlobWriter> _logger;

        public QueuedBlobWriter(
            IBlobWriterFactory blobWriterFactory,
            BlobWorkerPump queue,
            ILogger<QueuedBlobWriter> logger)
        {
            _underlyingWriter = blobWriterFactory?.CreateBlobWriter() ?? throw new ArgumentNullException(nameof(blobWriterFactory));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _logger.LogInformation(
                "[QueuedBlobWriter] Initialized - Underlying: {UnderlyingType}",
                _underlyingWriter.GetType().Name);
        }

        /// <summary>
        /// Returns a QueuedBlobStream that buffers writes and enqueues them on FlushAsync.
        /// </summary>
        public async Task<Stream> CreateBlobAndGetOutputStreamAsync(string containerName, string blobName, CancellationToken cancellationToken = default)
        {
            // Ensure container is initialized first
            await _underlyingWriter.InitClientAsync(containerName).ConfigureAwait(false);

            _logger.LogTrace(
                "[QueuedBlobWriter] Creating queued stream for {Container}/{Blob}",
                containerName, blobName);

            return new QueuedBlobStream(_queue, containerName, blobName, _logger);
        }

        // Pass-through methods - these don't benefit from queuing

        public Task UploadBlobAsync(string containerName, string blobName, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _underlyingWriter.UploadBlobAsync(containerName, blobName, data, cancellationToken);

        public Task<bool> BlobExistsAsync(string containerName, string blobName) =>
            _underlyingWriter.BlobExistsAsync(containerName, blobName);

        public Task<Stream> ReadBlobAsStreamAsync(string containerName, string blobName) =>
            _underlyingWriter.ReadBlobAsStreamAsync(containerName, blobName);

        public Task<bool> DeleteBlobAsync(string containerName, string blobName) =>
            _underlyingWriter.DeleteBlobAsync(containerName, blobName);

        // public Task<string> GenerateSasTokenAsync(string containerName, string blobName, TimeSpan expiryTime) =>
        //     _underlyingWriter.GenerateSasTokenAsync(containerName, blobName, expiryTime);

        public string GetBlobUri(string containerName, string blobName) =>
            _underlyingWriter.GetBlobUri(containerName, blobName);

        public Task<bool> InitClientAsync(string containerName) =>
            _underlyingWriter.InitClientAsync(containerName);

        public bool IsInitialized => _underlyingWriter.IsInitialized;

        public string GetConnectionInfo() =>
            _underlyingWriter.GetConnectionInfo() + " (Queued)";

        public void Dispose()
        {
            _underlyingWriter?.Dispose();
        }
    }
}
