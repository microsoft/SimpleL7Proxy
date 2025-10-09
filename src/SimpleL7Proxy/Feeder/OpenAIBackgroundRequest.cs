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
    public class OpenAIBackgroundRequest : IRequestProcessor
    {

        private readonly BackendOptions _options;
        private readonly ILogger<OpenAIBackgroundRequest> _logger;
        private readonly IRequestDataBackupService _backupService;
        private readonly IAsyncWorkerFactory _asyncWorkerFactory;


        public OpenAIBackgroundRequest(IOptions<BackendOptions> options,
                            IRequestDataBackupService backupService,
                            IAsyncWorkerFactory asyncWorkerFactory,
                            ILogger<OpenAIBackgroundRequest> logger)
        {
            _options = options.Value;
            _backupService = backupService;
            _asyncWorkerFactory = asyncWorkerFactory;
            _logger = logger;
        }

        // This runs as the ProxyWorker to rehydrate the request after recovery.. In this case, we just change the URL to check on status
        public async Task HydrateRequestAsync(RequestData request)
        {

            _logger.LogInformation($"OpenAIBackgroundRequest: Hydrating request {request.Guid} to check on status.");

            await _backupService.RestoreIntoAsync(request);
            // restore the async fields:
            request.runAsync = true;
            request.AsyncTriggered = true;
            request.asyncWorker = _asyncWorkerFactory.CreateAsync(request, 0);

            // let asyncworker restore the blob streams
            await request.asyncWorker.RestoreAsync(isBackground: true);

            // Handle URLs with query parameters when appending BackgroundRequestId
            try
            {
                UriBuilder uriBuilder = new UriBuilder(request.FullURL);
                
                // Append BackgroundRequestId to the path, ensuring proper path structure
                string path = uriBuilder.Path.TrimEnd('/');
                uriBuilder.Path = $"{path}/{request.BackgroundRequestId}";
                
                // UriBuilder handles all the complexities of maintaining proper URL structure
                request.FullURL = uriBuilder.Uri.ToString();
                request.Method = "GET";
                request.Path = uriBuilder.Uri.PathAndQuery;
                
                _logger.LogDebug($"Updated URL for background request check: {request.FullURL}");
            }
            catch (UriFormatException ex)
            {
                _logger.LogError(ex, $"Error constructing URL for background request {request.Guid}: {request.FullURL}");
            }

            request.AsyncHydrated = true; // mark it as hydrated from async
            request.Headers.Add("Content-Type", "application/json");


            // RequestData request = new RequestData(data.guid, data.guid, MID, Path, Method, Timestamp, Headers)
            // {
            //     AsyncBlobAccessTimeoutSecs = this.AsyncBlobAccessTimeoutSecs,
            //     Attempts = Attempts,
            //     BlobContainerName = BlobContainerName,
            //     DequeueTime = DequeueTime,
            //     EnqueueTime = EnqueueTime,
            //     ExpiresAt = ExpiresAt,
            //     FullURL = FullURL,
            //     incompleteRequests = IncompleteRequests,
            //     ParentId = ParentId,
            //     Priority = Priority,
            //     Priority2 = Priority2,
            //     profileUserId = this.profileUserId,
            //     Requeued = Requeued,
            //     SBTopicName = SBTopicName,
            //     Timeout = Timeout,
            //     UserID = UserID
            // };

        }

    }
}