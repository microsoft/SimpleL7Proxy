using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Backend;

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
        private readonly BackendOptions _backendOptions;

        public AsyncWorkerFactory(IBlobWriter blobWriter,
                                  ILogger<AsyncWorker> logger,
                                  IRequestDataBackupService requestBackupService,
                                  IOptions<BackendOptions> backendOptions)
        {
            _blobWriter = blobWriter;
            _logger = logger;
            _requestBackupService = requestBackupService;
            _backendOptions = backendOptions.Value;
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
            var worker = new AsyncWorker(requestData, AsyncTriggerTimeout, _blobWriter, _logger, _requestBackupService);
            return worker;
        }
    }
}