using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Proxy;

namespace SimpleL7Proxy.Async;

/// <summary>
/// Singleton bag of construction-time dependencies for <see cref="AsyncWorker"/>.
///
/// Roles:
///   AsyncInitializer     — one-shot startup: ensures server-scoped blob container exists,
///                          loads templates, wires <see cref="RequestData"/> statics. Runs
///                          once before traffic; never participates in per-request flow.
///   AsyncWorkerContext   — shared toolbox consumed every time an AsyncWorker is constructed.
///                          Carries the file store (queued small blobs), logger, and
///                          ProxyConfig. The streaming store is wired into AsyncWorker via
///                          its static Initialize(), not through this context. No init
///                          logic, no per-request state.
/// </summary>
public sealed class AsyncWorkerContext
{
    public IAsyncFileStore FileStore { get; }
    public IRequestSerializerService BackupService { get; }
    public ILogger<AsyncWorker> Logger { get; }
    public ProxyConfig Options { get; }
    public TemplateLoader Messages { get; }

    public AsyncWorkerContext(
        IAsyncFileStore fileStore,
        IRequestSerializerService backupService,
        ILogger<AsyncWorker> logger,
        IOptions<ProxyConfig> options,
        TemplateLoader messages)
    {
        FileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        BackupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
    }
}
