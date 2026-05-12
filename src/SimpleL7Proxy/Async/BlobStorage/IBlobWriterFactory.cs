namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Factory for creating <see cref="IBlobWriter"/> instances.
    ///
    /// Implementations are storage-backend-specific (Azure Blob Storage, AWS S3, etc.).
    /// The active implementation is selected by the reflection-driven DI map in
    /// <c>Program.RegisterAsyncDI</c> (via the <c>AsyncClassNames</c> config string).
    ///
    /// Consumers that only need to write should inject <see cref="IQueuedBlobWriter"/>
    /// instead — it goes through the shared BlobWriteQueue (batching, dedup, worker pool).
    /// Inject this factory only when you need a dedicated raw writer that bypasses the
    /// queue (e.g. <c>AsyncStreamingStore</c> for multi-GB response bodies, or
    /// <c>BlobWorkerPump</c> itself which is the queue's drainer).
    /// </summary>
    public interface IBlobWriterFactory
    {
        /// <summary>
        /// Creates a raw <see cref="IBlobWriter"/> bound to the configured backend. Bypasses
        /// the BlobWriteQueue — intended for streaming/large-payload scenarios where queue
        /// batching/dedup is undesirable, and for the BlobWorkerPump's own backing writer.
        /// </summary>
        IBlobWriter CreateBlobWriter();

        /// <summary>
        /// Creates an <see cref="IQueuedBlobWriter"/> that funnels writes through the shared
        /// BlobWriteQueue (batching, dedup, worker-pool concurrency). This is the standard
        /// write path for small one-shot blobs (headers, status messages, request snapshots).
        /// </summary>
        IQueuedBlobWriter CreateQueuedBlobWriter();

        /// <summary>
        /// Short human-readable status string describing the configured backend and credential
        /// mode (e.g. <c>"MI, https://acct.blob.core.windows.net"</c>, <c>"CS"</c>, <c>"Disabled"</c>).
        /// Surfaced in health output and startup logs.
        /// </summary>
        string InitStatus { get; }
    }
}
