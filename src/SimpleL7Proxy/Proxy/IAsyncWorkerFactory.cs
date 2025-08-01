using Microsoft.Extensions.Logging;
using SimpleL7Proxy.BlobStorage;

namespace SimpleL7Proxy.Proxy
{
    public interface IAsyncWorkerFactory
    {
        Task<AsyncWorker> CreateAsync(RequestData requestData);
    }

    public class AsyncWorkerFactory : IAsyncWorkerFactory
    {
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<AsyncWorker> _logger;

        public AsyncWorkerFactory(IBlobWriter blobWriter, ILogger<AsyncWorker> logger)
        {
            _blobWriter = blobWriter;
            _logger = logger;
        }

        public async Task<AsyncWorker> CreateAsync(RequestData requestData)
        {
            var worker = new AsyncWorker(requestData, _blobWriter, _logger);
            return worker;
        }
    }
}