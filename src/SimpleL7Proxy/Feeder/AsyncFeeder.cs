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

using SimpleL7Proxy.Backend;
using Shared.RequestAPI.Models;
using System.Security.Policy;
using SimpleL7Proxy.ServiceBus;

namespace SimpleL7Proxy.Feeder
{
    public class AsyncFeeder : IHostedService, IAsyncFeeder
    {

        private readonly BackendOptions _options;
        private readonly ILogger<AsyncFeeder> _logger;
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isShuttingDown = false;
        private Task? readerTask;
        CancellationTokenSource? _cancellationTokenSource;
        private readonly ServiceBusFactory _senderFactory;

        // Batch tuning
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public AsyncFeeder(IOptions<BackendOptions> options, ServiceBusFactory senderFactory,ILogger<AsyncFeeder> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory;
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
            isShuttingDown = true;
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
            Console.WriteLine($"{jobStatus}");
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