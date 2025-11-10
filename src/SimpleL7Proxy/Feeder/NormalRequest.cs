
using Azure.Core;
using Azure.Identity;
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
using SimpleL7Proxy.ServiceBus;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Proxy;
using SimpleL7Proxy.User;
using SimpleL7Proxy.Queue;

namespace SimpleL7Proxy.Feeder
{
    public class NormalRequest : IRequestProcessor
    {
        private readonly BackendOptions _options;
        private readonly ILogger<NormalRequest> _logger;
        private readonly IRequestDataBackupService _backupService;
        private readonly IAsyncWorkerFactory _asyncWorkerFactory;


        public NormalRequest(IOptions<BackendOptions> options,
                            IRequestDataBackupService backupService,
                            IAsyncWorkerFactory asyncWorkerFactory,
                            ILogger<NormalRequest> logger)
        {
            _options = options.Value;
            _backupService = backupService;
            _asyncWorkerFactory = asyncWorkerFactory;
            _logger = logger;
        }

        // This runs as the ProxyWorker to rehydrate the request from Blob storage
        public async Task HydrateRequestAsync(RequestData request)
        {

            // restore the request from blob storage, re-create the async streams.
            await DataFromBlob(request);

            request.Requeued = true; // mark it as requeued
            request.AsyncHydrated = true; // mark it as hydrated from async
        }
            

        private async Task DataFromBlob(RequestData request)
        {
            if ( request.BodyBytes == null || request.BodyBytes.Length == 0)
            {
                // populate the fields that were stored in the backup blob
                await _backupService.RestoreIntoAsync(request);
            }
            // restore the async fields:
            request.runAsync = true;
            request.AsyncTriggered = true;

            _logger.LogDebug("Creating async worker for request {Guid} URL: {FullURL} UserId: {UserID} ",
                request.Guid, request.FullURL, request.UserID);
            request.asyncWorker = _asyncWorkerFactory.CreateAsync(request, 0);

            // let asyncworker restore the blob streams
            await request.asyncWorker.RestoreAsync();
        }

    }
}