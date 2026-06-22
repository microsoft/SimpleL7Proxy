
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
    public class AsyncRequestStatus : IRequestProcessor
    {
        private readonly ProxyConfig _options;
        private readonly ILogger<AsyncRequestStatus> _logger;
        private readonly IRequestSerializerService _backupService;
        private readonly AsyncWorkerContext _asyncWorkerContext;


        public AsyncRequestStatus(IOptions<ProxyConfig> options,
                            IRequestSerializerService backupService,
                            AsyncWorkerContext asyncWorkerContext,
                            ILogger<AsyncRequestStatus> logger)
        {
            _options = options.Value;
            _backupService = backupService;
            _asyncWorkerContext = asyncWorkerContext;
            _logger = logger;
        }

        public async Task<int> CheckStatus(RequestData request)
        {
            string baseURL = _options.RequestAPIBaseUri?.ToString() ?? throw new InvalidOperationException("RequestAPIBaseUri is not configured.");

           // baseURL = "https://nvmrequestapi.azurewebsites.net";

            // The client-supplied Guid (the one whose status we want) lives in the
            // "Guid" header. request.Guid is a fresh server-generated id for *this*
            // status-check request and is unrelated to the original async request.
            var lookupGuid = request.Headers["Guid"];
            if (string.IsNullOrEmpty(lookupGuid))
            {
                _logger.LogWarning("CheckStatus called without a 'Guid' header on request {Guid}", request.Guid);
                return 0;
            }

            _logger.LogDebug("AsyncRequestStatus: Checking status of request {LookupGuid}.", lookupGuid);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseURL}/api/checkStatus/{lookupGuid}");
            var response = await _options.Client!.SendAsync(httpRequest).ConfigureAwait(false);
            var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CheckStatus for {LookupGuid} returned {Status}: {Body}",
                    lookupGuid, (int)response.StatusCode, jsonResponse);
                return 0;
            }

            CheckStatusResponse? checkStatusResponse = null;
            try
            {
                checkStatusResponse = JsonSerializer.Deserialize<CheckStatusResponse>(jsonResponse);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "CheckStatus for {LookupGuid} returned non-JSON body: {Body}",
                    lookupGuid, jsonResponse);
                return 0;
            }

            Console.WriteLine($"Status of request: {jsonResponse}  status: {checkStatusResponse?.Status}  Count: {checkStatusResponse?.CheckCount}");

            return checkStatusResponse?.CheckCount ?? 0;
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
            request.asyncWorker = new AsyncWorker(request, 0, _asyncWorkerContext);

            // let asyncworker restore the blob streams
            await request.asyncWorker.PrepareResponseStreamsAsync();
        }

    }
}