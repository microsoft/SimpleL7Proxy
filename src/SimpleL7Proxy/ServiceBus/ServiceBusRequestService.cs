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
            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            stoppingToken.Register(() =>
            {
                _logger.LogInformation("ServiceBus Reader service stopping.");
            });


            // Initialize Service Bus Sender
            if (_options.UseServiceBus )
            {
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

            _logger.LogInformation("Starting ServiceBusRequestService service...");

            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {   while (!stoppingToken.IsCancellationRequested)
                    {
                        // Check if there are any messages in the status queue
                        if (_statusQueue.TryDequeue(out var statusMessage))
                        {
                            // Process the status message
                            await _senderFactory.GetSender("status").SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(statusMessage)), stoppingToken);
                        }
                        else {
                            break;
                        }
                    }


                    //     // Process the status message
                    // // Example message payload
                    // var messagePayload = new { Id = Guid.NewGuid(), Timestamp = DateTime.UtcNow, Message = "Hello, Azure Service Bus!" };
                    // string messageBody = JsonSerializer.Serialize(messagePayload);

                    // // Send message to the topic
                    // await SendMessageToTopicAsync("example-topic", messageBody, stoppingToken);


                    // Wait for 5 seconds before sending the next message
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred while sending a message to the topic.");
                }
            }

            _logger.LogInformation("AzureServiceBusServer is stopping.");
        }

        
        private async Task SendMessageToTopicAsync(string topicName, string messageBody, CancellationToken cancellationToken)
        {
            var sender = _senderFactory.GetSender(topicName);
            var message = new ServiceBusMessage(messageBody);

            await sender.SendMessageAsync(message, cancellationToken);
        }
    }
}