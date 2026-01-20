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

            _logger.LogDebug($"OpenAIBackgroundRequest: Hydrating request {request.Guid} to check on status.");

            await _backupService.RestoreIntoAsync(request);
            
            // Validate that required fields were restored
            if (string.IsNullOrEmpty(request.profileUserId))
            {
                _logger.LogError("OpenAIBackgroundRequest: profileUserId is null or empty after restore for request {Guid}", request.Guid);
                throw new InvalidOperationException($"Failed to restore profileUserId for request {request.Guid}");
            }
            
            if (string.IsNullOrEmpty(request.BlobContainerName))
            {
                _logger.LogError("OpenAIBackgroundRequest: BlobContainerName is null or empty after restore for request {Guid}", request.Guid);
                throw new InvalidOperationException($"Failed to restore BlobContainerName for request {request.Guid}");
            }
            
            // restore the async fields:
            request.IsBackgroundCheck = true;
            request.runAsync = true;
            request.AsyncTriggered = true;
            request.asyncWorker = _asyncWorkerFactory.CreateAsync(request, 0);

            // Initialize for background check - blobs will be created lazily when first written to
            await request.asyncWorker.InitializeForBackgroundCheck();

            // Transform URL from POST /resp/responses?api-version=... to GET /resp/v1/responses/{backgroundRequestId}
            try
            {
                UriBuilder uriBuilder = new UriBuilder(request.FullURL);
                
                // Check if the BackgroundRequestId is already in the path
                if (uriBuilder.Path.Contains($"/{request.BackgroundRequestId}"))
                {
                    _logger.LogDebug($"URL already contains request ID: {uriBuilder.Uri}");
                }
                else
                {
                    // Transform the path: /resp/responses -> /resp/v1/responses/{backgroundRequestId}
                    // Remove trailing slashes and handle path replacement
                    string basePath = uriBuilder.Path.TrimEnd('/');
                    
                    // Replace "responses" with "v1/responses/{backgroundRequestId}"
                    if (basePath.EndsWith("/responses"))
                    {
                        basePath = basePath.Substring(0, basePath.LastIndexOf("/responses"));
                        uriBuilder.Path = $"{basePath}/v1/responses/{request.BackgroundRequestId}";
                    }
                    else
                    {
                        // Fallback: just append the background request ID
                        uriBuilder.Path = $"{basePath}/{request.BackgroundRequestId}";
                    }
                    
                    // Remove query parameters for status check
                    uriBuilder.Query = string.Empty;
                    
                    _logger.LogDebug($"Transformed URL for background check: {uriBuilder.Uri}");
                }
                
                request.FullURL = uriBuilder.Uri.ToString();
                request.Path = new Uri(request.FullURL).PathAndQuery;
                request.Method = "GET";
                
                _logger.LogDebug($"Updated for background check - FullURL: {request.FullURL}, Path: {request.Path}");
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