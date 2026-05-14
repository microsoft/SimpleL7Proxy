namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Marker interface for an <see cref="IBlobWriter"/> that funnels writes through the
    /// shared <see cref="BlobWorkerPump"/> (batching, dedup, worker-pool concurrency).
    ///
    /// Inject this (instead of <see cref="IBlobWriter"/>) when a consumer specifically
    /// needs the queued write path for small one-shot blobs (headers, status messages,
    /// request snapshots). Consumers that need the raw streaming path should inject
    /// <see cref="IBlobWriterFactory"/> and call <c>CreateBlobWriter()</c>.
    /// </summary>
    public interface IQueuedBlobWriter : IBlobWriter
    {
    }
}
