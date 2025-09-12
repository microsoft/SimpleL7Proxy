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

namespace SimpleL7Proxy.Feeder
{
    public class AsyncFeeder : IHostedService, IAsyncFeeder
    {

        private readonly BackendOptions _options;
        private readonly ILogger<AsyncFeeder> _logger;
        private readonly IRequestDataBackupService _backupService;
        private readonly IAsyncWorkerFactory _asyncWorkerFactory; // Just inject the factory
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
                            ILogger<AsyncFeeder> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory;
            _backupService = backupService;
            _asyncWorkerFactory = asyncWorkerFactory;
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
            var jobStatus = message.Body.ToString();

            var request = JsonSerializer.Deserialize<RequestAPIDocument>(jobStatus);
            if (request != null && !string.IsNullOrEmpty(request.guid))
            {
                var oldRequest = await _backupService.RestoreAsync(request.guid);
                if (oldRequest == null)
                {
                    _logger.LogWarning("AsyncFeeder: Could not find backup for async request with ID: {Id}, MID: {Mid}, Status: {Status}", request.id, request.mid, request.status);
                    await args.CompleteMessageAsync(message);
                    return;
                }

                RequestData rd = oldRequest.toRequestData();
                _logger.LogInformation("AsyncFeeder: Enqueuing async request with ID: {Id}, MID: {Mid}, Status: {Status}", request.id, request.mid, request.status);

                // restore the async fields:
                rd.runAsync = true;
                rd.asyncWorker = _asyncWorkerFactory.CreateAsync(rd, 0);
                await rd.asyncWorker.RestoreAsync();


                // DO server stuff here

                // Requeue it here
                
   
            }
            else
            {
                _logger.LogWarning("AsyncFeeder: Received invalid message that could not be deserialized to RequestAPIDocument.");
                Console.WriteLine($"{jobStatus}");
            }

            await args.CompleteMessageAsync(message);
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