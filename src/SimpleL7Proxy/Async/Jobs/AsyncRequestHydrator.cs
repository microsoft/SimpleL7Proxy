
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using System.Security.Policy;

using SimpleL7Proxy.Config;
using Shared.RequestAPI.Models;
using SimpleL7Proxy.Async.ServiceBus;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Queue;

namespace SimpleL7Proxy.Async.Jobs
{
    public class AsyncRequestHydrator : IRequestProcessor
    {
        private readonly ProxyConfig _options;
        private readonly ILogger<AsyncRequestHydrator> _logger;
        private readonly IRequestSerializerService _backupService;
        private readonly AsyncWorkerContext _asyncWorkerContext;


        public AsyncRequestHydrator(IOptions<ProxyConfig> options,
                            IRequestSerializerService backupService,
                            AsyncWorkerContext asyncWorkerContext,
                            ILogger<AsyncRequestHydrator> logger)
        {
            _options = options.Value;
            _backupService = backupService;
            _asyncWorkerContext = asyncWorkerContext;
            _logger = logger;
        }

        // This runs as the ProxyWorker to rehydrate the request from Blob storage
        public async Task HydrateRequestAsync(RequestData request)
        {

            // restore the request from blob storage, re-create the async streams.
            await DataFromBlob(request);

            // Reset per-attempt counters that were persisted from the prior process.
            // Without this, the worker's `BackendAttempts < maxSharedAttempts` guard
            // short-circuits (e.g. SinglePass + 1 host => maxSharedAttempts = 1, but
            // BackendAttempts is already 1 from before the crash) and the request fails
            // with 503 "No Active Hosts Available" without ever calling a backend.
            request.BackendAttempts = 0;
            // request.incompleteRequests?.Clear();

            request.Requeued = true; // mark it as requeued
            request.AsyncHydrated = true; // mark it as hydrated from async
        }
            

        private async Task DataFromBlob(RequestData request)
        {
            if (request.BodyBytes == null || request.BodyBytes.Value.Length == 0)
            {
                // populate the fields that were stored in the backup blob
                await _backupService.RestoreIntoAsync(request);
            }
            // restore the async fields:
            request.runAsync = true;
            request.AsyncTriggered = true;

            _logger.LogDebug("Creating async worker for request {Guid} URL: {FullURL} UserId: {UserID} ",
                request.Guid, request.FullURL, request.UserID);
            request.asyncWorker = new AsyncWorker(request, 0, _asyncWorkerContext);

            // let asyncworker restore the blob streams
            await request.asyncWorker.PrepareResponseStreamsAsync();
        }

    }
}