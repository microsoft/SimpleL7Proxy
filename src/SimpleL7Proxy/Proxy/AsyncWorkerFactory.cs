using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.BackupAPI;

namespace SimpleL7Proxy.Proxy
{
    public class AsyncWorkerFactory : IAsyncWorkerFactory
    {
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<AsyncWorker> _logger;  
        private readonly IRequestDataBackupService _requestBackupService;
        private readonly IBackupAPIService _backupAPIService;

        private readonly ProxyConfig _backendOptions;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        public AsyncWorkerFactory(IBlobWriter blobWriter,
                                  ILogger<AsyncWorker> logger,
                                  IRequestDataBackupService requestBackupService,
                                  IOptions<ProxyConfig> backendOptions,
                                  IBackupAPIService backupAPIService)
        {
            _blobWriter = blobWriter;
            _logger = logger;
            _requestBackupService = requestBackupService;
            _backendOptions = backendOptions.Value;
            _backupAPIService = backupAPIService;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized) return;
                await _blobWriter.InitClientAsync(Constants.Server, Constants.Server).ConfigureAwait(false);
                _initialized = true;
            }
            catch (BlobWriterException ex)
            {
                _backendOptions.AsyncModeEnabled = false;
                _logger.LogError(ex, "Failed to initialize BlobWriter in AsyncWorkerFactory, disabling Async mode");
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task<AsyncWorker> CreateAsync(RequestData requestData, int AsyncTriggerTimeout)
        {
            // Ensure blob client is initialized (lazy, thread-safe, one-time)
            await EnsureInitializedAsync().ConfigureAwait(false);

            _logger.LogDebug("[AsyncWorkerFactory] Creating AsyncWorker for request {Guid} with timeout {Timeout}s", 
                requestData.Guid, AsyncTriggerTimeout);

            return new AsyncWorker(requestData, AsyncTriggerTimeout, _blobWriter, _logger, _requestBackupService, _backendOptions);
        }
    }
}