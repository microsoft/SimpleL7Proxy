using Microsoft.Extensions.Logging;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.Storage;

namespace SimpleL7Proxy.Proxy
{
    public interface IAsyncWorkerFactory
    {
        AsyncWorker CreateAsync(RequestData requestData);
    }

    public class AsyncWorkerFactory : IAsyncWorkerFactory
    {
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<AsyncWorker> _logger;
        private readonly IRequestStorageService _requestStorageService;

        public AsyncWorkerFactory(IBlobWriter blobWriter, ILogger<AsyncWorker> logger, IRequestStorageService requestStorageService)
        {
            _blobWriter = blobWriter;
            _logger = logger;
            _requestStorageService = requestStorageService;
        }

        public AsyncWorker CreateAsync(RequestData requestData)
        {
            var worker = new AsyncWorker(requestData, _blobWriter, _logger, _requestStorageService);
            return worker;
        }
    }
}