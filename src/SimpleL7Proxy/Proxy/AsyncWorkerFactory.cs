using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Proxy
{
    public class AsyncWorkerFactory : IAsyncWorkerFactory
    {
        private readonly IAsyncRequestStore _requestStore;
        private readonly ILogger<AsyncWorker> _logger;  

        private readonly ProxyConfig _backendOptions;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private bool _initialized;

        public AsyncWorkerFactory(IAsyncRequestStore requestStore,
                                  ILogger<AsyncWorker> logger,
                                  IOptions<ProxyConfig> backendOptions)
        {
            _requestStore = requestStore;
            _logger = logger;
            _backendOptions = backendOptions.Value;
        }

        private async Task EnsureInitializedAsync()
        {
            if (_initialized) return;

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized) return;
                await _requestStore.InitializeClientAsync(Constants.Server, Constants.Server).ConfigureAwait(false);
                _initialized = true;
            }
            catch (Exception ex)
            {
                _backendOptions.AsyncModeEnabled = false;
                _logger.LogError(ex, "Failed to initialize async request store in AsyncWorkerFactory, disabling Async mode");
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

            return new AsyncWorker(requestData, AsyncTriggerTimeout, _requestStore, _logger, _backendOptions);
        }
    }
}