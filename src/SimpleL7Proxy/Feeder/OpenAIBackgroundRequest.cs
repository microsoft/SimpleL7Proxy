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

using SimpleL7Proxy.Backend;
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

            request.FullURL = "https://api.openai.com/v1/responses" + "/" + request.BackgroundRequestId;
            request.Method = "GET";
            request.Path = new Uri(request.FullURL).PathAndQuery;

            request.AsyncHydrated = true; // mark it as hydrated from async
            request.RequestAPIStatus = RequestAPIStatusEnum.PostProcessing;


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