using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Async.BlobStorage
{
    /// <summary>
    /// Backend-agnostic base for <see cref="IBlobWriterFactory"/> implementations.
    /// Owns the pieces that don't depend on the storage backend:
    ///   • the queued-writer decorator wiring (<see cref="CreateQueuedBlobWriter"/>),
    ///   • the no-op fallback writer used when the backend is disabled or misconfigured,
    ///   • the <see cref="Lazy{T}"/> indirection that breaks the DI cycle between the
    ///     factory and <see cref="BlobWorkerPump"/>,
    ///   • the <see cref="InitStatus"/> property.
    ///
    /// Subclasses only need to implement <see cref="CreateBlobWriter"/> — the backend-
    /// specific construction (Azure SDK / AWS SDK / etc.). They should set
    /// <see cref="InitStatus"/> to a short human-readable status string and return
    /// <see cref="CreateNullWriter"/> when the backend is unavailable.
    /// </summary>
    public abstract class GenericBlobFactory : IBlobWriterFactory
    {
        protected readonly IOptionsMonitor<ProxyConfig> _optionsMonitor;
        protected readonly ILogger<NullBlobWriter> _nullBlobWriterLogger;
        // Lazy resolution avoids a DI cycle: BlobWorkerPump itself takes IBlobWriterFactory
        // (to get its raw underlying writer). The pump is only needed when callers ask for
        // a queued writer, so we defer its resolution until CreateQueuedBlobWriter() runs.
        private readonly Lazy<BlobWorkerPump> _workerPump;
        private readonly ILogger<QueuedBlobWriter> _queuedLogger;

        protected GenericBlobFactory(
            IOptionsMonitor<ProxyConfig> optionsMonitor,
            ILogger<NullBlobWriter> nullBlobWriterLogger,
            Lazy<BlobWorkerPump> workerPump,
            ILogger<QueuedBlobWriter> queuedLogger)
        {
            _optionsMonitor = optionsMonitor;
            _nullBlobWriterLogger = nullBlobWriterLogger;
            _workerPump = workerPump;
            _queuedLogger = queuedLogger;
            InitStatus = "Not initialized";
        }

        /// <inheritdoc/>
        public string InitStatus { get; protected set; }

        /// <inheritdoc/>
        public abstract IBlobWriter CreateBlobWriter();

        /// <summary>
        /// Builds a <see cref="QueuedBlobWriter"/> that wraps a fresh raw writer
        /// (from <see cref="CreateBlobWriter"/>) and routes writes through the shared
        /// <see cref="BlobWorkerPump"/>. Backend-agnostic — identical for every subclass.
        /// </summary>
        public IQueuedBlobWriter CreateQueuedBlobWriter()
        {
            return new QueuedBlobWriter(this, _workerPump.Value, _queuedLogger);
        }

        /// <summary>
        /// Helper for subclasses: returns the no-op writer used when async mode is disabled
        /// or the configured backend can't be reached. Centralized so every factory falls
        /// back to the same type (which is the one DI binds as <see cref="IQueuedBlobWriter"/>
        /// in non-async mode).
        /// </summary>
        protected IBlobWriter CreateNullWriter() => new NullBlobWriter(_nullBlobWriterLogger);
    }
}
