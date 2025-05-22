using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Backend;


namespace SimpleL7Proxy.ServiceBus
{

    public class ServiceBusRequestService : BackgroundService, IServiceBusRequestService
    {
        private readonly BackendOptions _options;
        private readonly ServiceBusSenderFactory _senderFactory;
        private readonly ILogger<ServiceBusRequestService> _logger;
        public static readonly ConcurrentQueue<ServiceBusStatusMessage> _statusQueue = new ConcurrentQueue<ServiceBusStatusMessage>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);

        public ServiceBusRequestService(IOptions<BackendOptions> options, ServiceBusSenderFactory senderFactory, ILogger<ServiceBusRequestService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            _logger = logger;
        }

        private void OnApplicationStopping()
        {
            _cancellationTokenSource?.Cancel();
        }

        CancellationTokenSource? _cancellationTokenSource;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                stoppingToken.Register(() =>
                {
                    _logger.LogInformation("ServiceBus Reader service stopping.");
                });

                // create a new task that reads the user config every hour
                return Task.Run(() => EventConsumer(stoppingToken), stoppingToken);                
            }

            return Task.CompletedTask;
        }

        public bool IsRunning => _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;


        public bool updateStatus(RequestData message)
        {
            try
            {
                _statusQueue.Enqueue(new ServiceBusStatusMessage(message.SBClientID, message.Guid, message.SBStatus.ToString()));
                _queueSignal.Release();

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue message to the status queue.");
                return false; // Enqueue failed
            }
        }


        public async Task EventConsumer(CancellationToken stoppingToken)
        {

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _queueSignal.WaitAsync(stoppingToken).ConfigureAwait(false);

                    // Check if there are any messages in the status queue
                    while (_statusQueue.TryDequeue(out var statusMessage))
                    {
                        // Process the status message
                        await _senderFactory.GetSender("status").SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(statusMessage)), stoppingToken);
                    }

                    // Process the message
                    //var message = new ServiceBusStatusMessage("ClientId", Guid.NewGuid(), "Status");
                    //await SendMessageToTopicAsync("status", JsonSerializer.Serialize(message), stoppingToken);

                }

                Console.WriteLine("Stopping ServiceBusRequestService service...");
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit gracefully
                _logger.LogInformation("ServiceBusRequestService task was canceled.");
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, exit gracefully
                _logger.LogInformation($"ServiceBusRequestService shutdown initiated: {_statusQueue.Count()} items need to be flushed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while sending a message to the topic.");
            }
            finally
            {
                // Flush all items

                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    if (_statusQueue.Count() % 100 == 0) _logger.LogInformation($"{_statusQueue.Count()} items remain to be flushed.");
                    // Process the status message
                    try
                    {
                        await _senderFactory.GetSender("status").SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(statusMessage)), stoppingToken);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while flushing service bus.  Continuing.");
                    }
                }
            }

            _logger.LogInformation("ServiceBusRequestService is stopping.");
        }


        private async Task SendMessageToTopicAsync(string topicName, string messageBody, CancellationToken cancellationToken)
        {
            var sender = _senderFactory.GetSender(topicName);
            var message = new ServiceBusMessage(messageBody);

            await sender.SendMessageAsync(message, cancellationToken);
        }
    }
}