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
    public class AsyncFeeder : IHostedService, IAsyncFeeder
    {

        private readonly BackendOptions _options;
        private readonly ILogger<AsyncFeeder> _logger;
        private readonly IRequestDataBackupService _backupService;
        private readonly IAsyncWorkerFactory _asyncWorkerFactory; // Just inject the factory
        private readonly IUserPriorityService _userPriority;
        private readonly IUserProfileService _userProfile;
        private readonly IConcurrentPriQueue<RequestData> _requestsQueue;

        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isShuttingDown = false;
        private Task? readerTask;
        CancellationTokenSource? _cancellationTokenSource;
        private readonly ServiceBusFactory _senderFactory;

        // Batch tuning
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public AsyncFeeder(IOptions<BackendOptions> options,
                            ServiceBusFactory senderFactory,
                            IRequestDataBackupService backupService,
                            IAsyncWorkerFactory asyncWorkerFactory,
                            IConcurrentPriQueue<RequestData> requestsQueue,
                            IUserPriorityService userPriority,
                            IUserProfileService userProfile,
                            ILogger<AsyncFeeder> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory;
            _backupService = backupService;
            _asyncWorkerFactory = asyncWorkerFactory;
            _userPriority = userPriority;
            _userProfile = userProfile;
            _requestsQueue = requestsQueue;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource.Token.Register(() =>
                {
                    _logger.LogCritical("AsyncFeeder service stopping.");
                });

                // Start the reader task but DON'T await it
                readerTask = Task.Run(() => EventReader(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Return immediately - let the reader task run in the background
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Only set the flag if we're not already shutting down
            if (!isShuttingDown)
            {
                _logger.LogCritical("Shutting down AsyncFeeder");
                isShuttingDown = true;
                return readerTask ?? Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        public async Task EventReader(CancellationToken token)
        {

            _logger.LogCritical("Starting AsyncFeeder service...");

            try
            {
                // Configure the message and error handler options in the options object
                var options = new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 1,
                    MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5)
                };

                // get a processor that we can use to receive the message
                var processor = _senderFactory.GetQueueProcessor("feeder", options);

                // add handler to process messages
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;

                await processor.StartProcessingAsync().ConfigureAwait(false);

                while (!isShuttingDown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }

                await processor.StopProcessingAsync().ConfigureAwait(false);
                await processor.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation("AsyncFeeder service has stopped processing messages.");

            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit gracefully
                _logger.LogInformation("AsyncFeeder service task was canceled.");
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, exit gracefully
                _logger.LogInformation($"AsyncFeeder service shutdown initiated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while calling AsyncFeeder service.: " + ex);
            }

            _logger.LogInformation("AsyncFeeder service is stopping.");
        }


        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            var message = args.Message;
            var messageFromSB = message.Body.ToString();

            try
            {

                // restore the request from blob storage, re-create the async streams.
                RequestData rd = await DataFromBlob(messageFromSB);
                if (rd == null)
                {
                    return;
                }

                rd.Requeued = true; // mark it as requeued
                rd.AsyncHyderated = true; // mark it as rehydrated from async
                bool doUserProfile = _options.UseProfiles;

                // re-establish job as an incoming request
                if (doUserProfile)
                {
                    _userPriority.addRequest(rd.Guid, rd.UserID);
                    int userPriorityBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue) ? 1 : 0;

                    _logger.LogInformation("AsyncFeeder: Enqueuing async request with ID: {Id}, MID: {Mid}", rd.Guid, rd.MID);

                    if (!_requestsQueue.Requeue(rd, rd.Priority, userPriorityBoost, rd.EnqueueTime))
                    {
                        _logger.LogWarning("AsyncFeeder: Failed to enqueue request with ID: {guid}", rd.Guid);
                    }
                }

                // mark the request as completed
                await args.CompleteMessageAsync(message);
            }

            // message will be retried automatically on error
            catch (Exception ex)
            {
                _logger.LogError(ex, "AsyncFeeder: Error processing message from Service Bus: " + ex.Message);
            }
        }

        private async Task<RequestData?> DataFromBlob(string messageFromSB)
        {
            var requestMsg = JsonSerializer.Deserialize<RequestAPIDocument>(messageFromSB);
            if (requestMsg != null && !string.IsNullOrEmpty(requestMsg.guid))
            {
                var restoredRequestData = await _backupService.RestoreAsync(requestMsg.guid);
                if (restoredRequestData == null)
                {
                    _logger.LogWarning("AsyncFeeder: Could not find backup for async request with ID: {Id}, MID: {Mid}, Status: {Status}", requestMsg.id, requestMsg.mid, requestMsg.status);
                    return null;
                }
                // restore the async fields:
                restoredRequestData.runAsync = true;
                restoredRequestData.AsyncTriggered = true;
                restoredRequestData.asyncWorker = _asyncWorkerFactory.CreateAsync(restoredRequestData, 0);
                await restoredRequestData.asyncWorker.RestoreAsync();

                return restoredRequestData;

            }
            else
            {
                _logger.LogWarning("AsyncFeeder: Received invalid message that could not be deserialized to RequestAPIDocument.");
            }

            return null;
        }

        private async Task ErrorHandler(ProcessErrorEventArgs args)
        {

            _logger.LogError(args.Exception, "Error in AsyncFeeder message handler: " + args.Exception.Message);
            // Handle the error (e.g., log it, send it to a monitoring system, etc.)


            await Task.Delay(100);
            //Console.WriteLine($"Error occurred: {args.Exception}");
        }
    }
}