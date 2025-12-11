using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.BackupAPI;

namespace SimpleL7Proxy.Proxy
{
    public interface IAsyncWorkerFactory
    {
        AsyncWorker CreateAsync(RequestData requestData, int AsyncTriggerTimeout);
    }

    public class AsyncWorkerFactory : IAsyncWorkerFactory
    {
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<AsyncWorker> _logger;  
        private readonly IRequestDataBackupService _requestBackupService;
        private readonly IBackupAPIService _backupAPIService;

        private readonly BackendOptions _backendOptions;

        public AsyncWorkerFactory(IBlobWriter blobWriter,
                                  ILogger<AsyncWorker> logger,
                                  IRequestDataBackupService requestBackupService,
                                  IOptions<BackendOptions> backendOptions,
                                  IBackupAPIService backupAPIService)
        {
            _blobWriter = blobWriter;
            _logger = logger;
            _requestBackupService = requestBackupService;
            _backendOptions = backendOptions.Value;
            _backupAPIService = backupAPIService;   
            try
            {
                _blobWriter.InitClientAsync(Constants.Server, Constants.Server).GetAwaiter().GetResult();
            }
            catch (BlobWriterException ex)
            {
                _backendOptions.AsyncModeEnabled = false;
                _logger.LogError(ex, "Failed to initialize BlobWriter in AsyncWorkerFactory, disabling Async mode");
                return;
            }
        }

        public AsyncWorker CreateAsync(RequestData requestData, int AsyncTriggerTimeout)
        {
            _logger.LogDebug("[AsyncWorkerFactory] Creating AsyncWorker for request {Guid} with timeout {Timeout}s", 
                requestData.Guid, AsyncTriggerTimeout);

            return new AsyncWorker(requestData, AsyncTriggerTimeout, _blobWriter, _logger, _requestBackupService, _backendOptions);
        }
    }
}