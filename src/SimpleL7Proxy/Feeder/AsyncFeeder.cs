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
using System.Runtime.Intrinsics.Arm;

namespace SimpleL7Proxy.Feeder
{
    public class AsyncFeeder : IHostedService, IAsyncFeeder
    {

        private readonly BackendOptions _options;
        private readonly ILogger<AsyncFeeder> _logger;
        private readonly IUserProfileService _userProfile;
        private readonly IRequestDataBackupService _requestBackupService;

        private readonly IRequestProcessor _normalRequest;
        private readonly IRequestProcessor _openAIRequest;
        private readonly IUserPriorityService _userPriority;
        private readonly IConcurrentPriQueue<RequestData> _requestsQueue;

        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isShuttingDown = false;
        private Task? readerTask;
        CancellationTokenSource? _cancellationTokenSource;
        private readonly ServiceBusFactory _senderFactory;
        private static long counter = 0;

        // REMOVE HARDCODED OPENAI CALL
        private static string openaicall = """
{
  "temperature": 1,
  "top_p": 1,
  "stop": null,
  "max_tokens": 4096,
  "presence_penalty": 0,
  "frequency_penalty": 0,
  "messages": [
    {
      "role": "system",
      "content": "You are an AI assistant that helps people find information."
    },
    {
      "role": "user",
      "content": "__CONTENT__"
    }
  ]
}
""";  // Closing the string literal

        // Batch tuning
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public AsyncFeeder(IOptions<BackendOptions> options,
                            IUserPriorityService userPriority,
                            IUserProfileService userProfile,
                            IRequestDataBackupService requestBackupService,
                            ServiceBusFactory senderFactory,
                            NormalRequest normalRequest,
                            OpenAIBackgroundRequest openAIRequest,
                            IConcurrentPriQueue<RequestData> requestsQueue,
                            ILogger<AsyncFeeder> logger)
        {
            _options = options.Value;
            _userPriority = userPriority;
            _userProfile = userProfile;
            _requestBackupService = requestBackupService;
            _senderFactory = senderFactory;
            _normalRequest = normalRequest;
            _openAIRequest = openAIRequest;
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
                _logger.LogInformation("[SHUTDOWN] ⏹ AsyncFeeder shutting down");
                isShuttingDown = true;
                return readerTask ?? Task.CompletedTask;
            }

            return Task.CompletedTask;
        }

        public async Task EventReader(CancellationToken token)
        {

            _logger.LogInformation("[SERVICE] ✓ AsyncFeeder service starting...");

            ServiceBusProcessor? processor = null;

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
                processor = _senderFactory.GetQueueProcessor("feeder", options);

                // add handler to process messages
                processor.ProcessMessageAsync += MessageHandler;
                processor.ProcessErrorAsync += ErrorHandler;

                try
                {
                    await processor.StartProcessingAsync().ConfigureAwait(false);
                    _logger.LogInformation("[SERVICE] ✓ AsyncFeeder successfully started processing messages from 'feeder' queue");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, 
                        "[ERROR] UnauthorizedAccessException: Cannot access Service Bus queue 'feeder'. " +
                        "Check that the managed identity has 'Azure Service Bus Data Receiver' role assigned to the queue. " +
                        "Service will continue but will not process messages.");
                    
                    // Clean up processor and wait for shutdown signal instead of throwing
                    await processor.DisposeAsync().ConfigureAwait(false);
                    processor = null;
                    
                    // Wait for shutdown signal
                    while (!isShuttingDown)
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    
                    _logger.LogInformation("[SHUTDOWN] ✓ AsyncFeeder stopped (no processor was running)");
                    return;
                }

                while (!isShuttingDown)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }

                await processor.StopProcessingAsync().ConfigureAwait(false);
                _logger.LogInformation("[SHUTDOWN] ✓ AsyncFeeder stopped processing messages");

            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit gracefully
                _logger.LogInformation("[SHUTDOWN] AsyncFeeder service task was canceled.");
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, exit gracefully
                _logger.LogInformation($"[SHUTDOWN] AsyncFeeder service shutdown initiated.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while calling AsyncFeeder service.: " + ex);
            }
            finally
            {
                // Ensure processor is disposed
                if (processor != null)
                {
                    try
                    {
                        await processor.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[SHUTDOWN] Error disposing processor");
                    }
                }
            }

            _logger.LogInformation("[SHUTDOWN] ✓ AsyncFeeder service stopped");
        }


        private async Task MessageHandler(ProcessMessageEventArgs args)
        {
            var message = args.Message;
            var messageFromSB = message.Body.ToString();
            var requestData = RequestDataParser.ParseRequestData(messageFromSB);

            try
            {

                // RequestAPIDocument comes from the status queue, only minimal fields populated
                if (requestData is RequestAPIDocument requestMsg)
                {
                    // this is either a status check on a background request, or a brand new request
                    bool isBackground = requestMsg.isBackground == true && requestMsg.status == RequestAPIStatusEnum.BackgroundProcessing;

                    var rd = ConvertDocumentToRequest(requestMsg, isBackground);
                    if (rd != null)
                    {
                        rd.RecoveryProcessor = isBackground ? _openAIRequest : _normalRequest;
                    }

                }
                else if (requestData is RequestMessage msg)
                {
                    var rd = await ConvertToNewRequestAsync(msg).ConfigureAwait(false);
                    if (rd != null)
                    {
                        rd.RecoveryProcessor = _normalRequest;
                    }

                    // Handle simple message
                    _logger.LogInformation("AsyncFeeder: UserID: {UserID}, ID: {Id}", msg.UserID, msg.Id);
                    //_logger.LogInformation("AsyncFeeder: Message content: {MessageContent}", messageFromSB);
                }

                else
                {
                    _logger.LogWarning("AsyncFeeder: Unknown message type received from Service Bus.");
                    _logger.LogInformation("AsyncFeeder: Message content: {MessageContent}", messageFromSB);
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

        public async Task<RequestData?> ConvertToNewRequestAsync(RequestMessage message)
        {
            Interlocked.Increment(ref counter);
            var requestId = _options.IDStr + "_Feeder_" + counter.ToString();
            var messageGuid = ((IRequestData)message).Guid;
            var guid = string.IsNullOrEmpty(messageGuid) ? Guid.NewGuid() : new Guid(messageGuid);

            RequestData rd = new("foo",
                guid, // Use the resolved guid instead of message.Guid
                requestId,
                message.Path ?? "/",
                "POST",
                DateTime.UtcNow,
                new Dictionary<string, string>(){["Content-Type"] = "application/json", ["S7PDEBUG"] = "true"});

            rd.FullURL = rd.Path; // for async requests, FullURL is same as Path

            rd.CalculateExpiration(_options.DefaultTTLSecs, _options.TTLHeader);

            rd.profileUserId = rd.UserID = message.UserID ?? string.Empty;

            var clientInfo = _userProfile.GetAsyncParams(rd.profileUserId);
            if (clientInfo != null)
            {
                rd.runAsync = true;
                rd.AsyncBlobAccessTimeoutSecs = clientInfo.AsyncBlobAccessTimeoutSecs;
                rd.BlobContainerName = clientInfo.ContainerName;
                rd.SBTopicName = clientInfo.SBTopicName;

                _logger.LogDebug("AsyncFeeder: Retrieved user profile for UserID: {UserID}. AsyncBlobAccessTimeoutSecs: {AsyncBlobAccessTimeoutSecs}, BlobContainerName: {BlobContainerName}, SBTopicName: {SBTopicName}",
                    rd.UserID, rd.AsyncBlobAccessTimeoutSecs, rd.BlobContainerName, rd.SBTopicName);
            } 
            else
            {
                _logger.LogWarning("AsyncFeeder: User profile not found for UserID: {UserID}. Using default async settings.", rd.UserID);
            }

            var bodyString =  openaicall.Replace("__CONTENT__", message.Body) ?? string.Empty;
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(bodyString);
            rd.setBody(bodyBytes);

            

            if (int.TryParse(message.Priority, out int priorityValue))
            {
                rd.Priority = priorityValue;
            }
            else
            {
                rd.Priority = 1; // Default priority
            }
            
            rd.IsBackground = false;
            rd.BackgroundRequestId = string.Empty;

            await _requestBackupService.BackupAsync(rd).ConfigureAwait(false);

            // re-establish job as an incoming request
            if (_options.UseProfiles)
            {
                _userPriority.addRequest(rd.Guid, rd.UserID);
                int userPriorityBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue) ? 1 : 0;

                _logger.LogDebug("AsyncFeeder: Enqueuing async request with ID: {Id}, MID: {Mid}, UserID: {UserID}", rd.Guid, rd.MID, rd.UserID);

                if (!_requestsQueue.Requeue(rd, rd.Priority, userPriorityBoost, rd.EnqueueTime))
                {
                    _logger.LogWarning("AsyncFeeder: Failed to enqueue request with ID: {guid}", rd.Guid);
                    return null;
                }

                return rd;
            }
            else
            {
                _logger.LogError("AsyncFeeder: User profiles are disabled, cannot process async request with ID: {Id}, MID: {Mid}", rd.Guid, rd.MID);
                return null;
            }
        }

        // Convert the minimal RequestAPIDocument from the queue into a full RequestData by restoring from blob storage

        public RequestData? ConvertDocumentToRequest(RequestAPIDocument data, bool isBackground)
        {
            try
            {

                RequestData rd = new RequestData(data.id!,
                                                 new Guid(data.guid!),
                                                 data.mid!,
                                                 "empty",       // path will be filled in by ProxyWorker
                                                 "empty",       // method will be filled in by ProxyWorker
                                                 data.createdAt == null ? DateTime.UtcNow : data.createdAt.Value,
                                                 []);          // headers will be filled in by ProxyWorker

                rd.UserID = data.userID!;
                rd.Priority = data.priority1 ?? 1;
                rd.IsBackground = data.isBackground ?? false;
                rd.BackgroundRequestId = data.backgroundRequestId ?? string.Empty;

                // re-establish job as an incoming request
                if (_options.UseProfiles)
                {
                    _userPriority.addRequest(rd.Guid, rd.UserID);
                    int userPriorityBoost = _userPriority.boostIndicator(rd.UserID, out float boostValue) ? 1 : 0;

                    _logger.LogInformation("AsyncFeeder: Enqueuing async request with ID: {Id}, MID: {Mid}, UserID: {UserID}", rd.Guid, rd.MID, rd.UserID);

                    if (!_requestsQueue.Requeue(rd, rd.Priority, userPriorityBoost, rd.EnqueueTime))
                    {
                        _logger.LogWarning("AsyncFeeder: Failed to enqueue request with ID: {guid}", rd.Guid);
                        return null;
                    }

                    return rd;
                }
                else
                {
                    _logger.LogError("AsyncFeeder: User profiles are disabled, cannot process async request with ID: {Id}, MID: {Mid}", rd.Guid, rd.MID);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AsyncFeeder: Error processing async request from Service Bus: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return null;
            }
        }
        // private async Task<RequestData?> DataFromBlob(RequestAPIDocument requestMsrdg)
        // {
        //     var restoredRequestData = await _backupService.RestoreAsync(requestMsg.guid);
        //     if (restoredRequestData == null)
        //     {
        //         _logger.LogWarning("AsyncFeeder: Could not find backup for async request with ID: {Id}, MID: {Mid}, Status: {Status}", requestMsg.id, requestMsg.mid, requestMsg.status);
        //         return null;
        //     }
        //     // restore the async fields:
        //     restoredRequestData.runAsync = true;
        //     restoredRequestData.AsyncTriggered = true;
        //     restoredRequestData.asyncWorker = _asyncWorkerFactory.CreateAsync(restoredRequestData, 0);
        //     await restoredRequestData.asyncWorker.RestoreAsync();

        //     return restoredRequestData;
        // }

        private async Task ErrorHandler(ProcessErrorEventArgs args)
        {
            if (args.Exception.Message.Contains("AzureCliCredential authentication failed"))
            {
                _logger.LogError("AzureCliCredential authentication failed while receiving AsyncFeeder messages.");
            }
            else if (args.Exception is UnauthorizedAccessException)
            {
                _logger.LogError(args.Exception, 
                    "UnauthorizedAccessException in AsyncFeeder: The identity does not have permission to access Service Bus queue 'feeder'. " +
                    "Ensure the managed identity has 'Azure Service Bus Data Receiver' role assigned.");
            }
            else 
            {
                _logger.LogError(args.Exception, "Error in AsyncFeeder message handler: " + args.Exception.Message);
            }
            // Handle the error (e.g., log it, send it to a monitoring system, etc.)


            await Task.Delay(100);
            //Console.WriteLine($"Error occurred: {args.Exception}");
        }
    }
}