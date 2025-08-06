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

    public class ServiceBusRequestService : IHostedService, IServiceBusRequestService
    {
        private readonly BackendOptions _options;
        private readonly ServiceBusSenderFactory _senderFactory;
        private readonly ILogger<ServiceBusRequestService> _logger;
        public static readonly ConcurrentQueue<ServiceBusStatusMessage> _statusQueue = new ConcurrentQueue<ServiceBusStatusMessage>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isRunning = false;
        private bool isShuttingDown = false;
        private Task? writerTask;

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


        //protected override Task ExecuteAsync(CancellationToken stoppingToken)
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _logger.LogCritical("ServiceBusRequestService starting...");
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource.Token.Register(() =>
                {
                    _logger.LogCritical("ServiceBus writer service stopping.");
                });

                isRunning = true;

                // Start the writer task but DON'T await it
                writerTask = Task.Run(() => EventWriter(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Return immediately - let the writer task run in the background
            }
            
            return Task.CompletedTask;
        }

        public bool IsRunning => _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;


        public bool updateStatus(RequestData message)
        {
            try
            {
                //_logger.LogInformation($"Enqueuing status message for UserId: {message.MID}, Status: {message.SBStatus} for Topic: {message.SBTopicName}");
                _statusQueue.Enqueue(new ServiceBusStatusMessage(message.Guid, message.SBTopicName, message.SBStatus.ToString()));
                _queueSignal.Release();

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue message to the status queue.");
                return false; // Enqueue failed
            }
        }

        int feeders = 5;

        public async Task EventWriter(CancellationToken token)
        {

            _logger.LogCritical("Starting ServiceBus writer service...");

            try
            {
                Task[] tasks = new Task[feeders];
                for (int i = 0; i < feeders; i++)
                {
                    // Start a new task for each feeder
                    tasks[i] = Task.Run(() => FeederTask(token), token);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
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
                _logger.LogError(ex, "An error occurred while sending a message to the topic.: " + ex);
            }
            finally
            {
                // Flush all items

                var cts = new CancellationTokenSource().Token;
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    if (_statusQueue.Count() % 100 == 0)
                        _logger.LogInformation($"{_statusQueue.Count()} items remain to be flushed.");

                    try
                    {
                        await _senderFactory.
                            GetSender(statusMessage.topicName).
                            SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(statusMessage)), cts);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while flushing service bus.  Continuing." + e);
                    }
                }
            }

            _logger.LogInformation("ServiceBusRequestService is stopping.");
        }

        private async Task FeederTask(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!isShuttingDown && _statusQueue.IsEmpty)
                {
                    await _queueSignal.WaitAsync(token).ConfigureAwait(false);
                }

                // Check if there are any messages in the status queue
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    //_logger.LogInformation("Need to send status message to topic: {TopicName}", statusMessage.topicName);
                    // Process the status message
                    await _senderFactory.
                        GetSender(statusMessage.topicName).
                        SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(statusMessage)), token);

                    if (!isShuttingDown)
                    {
                        await Task.Delay(100, token).ConfigureAwait(false); // Wait for 1/10 second
                    }
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            isShuttingDown = true;
            if (_options.AsyncModeEnabled)
            {

                _logger.LogCritical("ServiceBusRequestService: Flushing {events} events before stopping...", _statusQueue.Count);
                while (isRunning && _statusQueue.Count > 0)
                {
                    Task.Delay(100).Wait();
                }

                _cancellationTokenSource?.Cancel();
                isRunning = false;
                writerTask?.Wait(cancellationToken);
            }

            return Task.CompletedTask;
        }

        // TaskCompletionSource<bool> ShutdownTCS = new();

        //     private async Task SendMessageToTopicAsync(string topicName, string messageBody, CancellationToken cancellationToken)
        //     {
        //         var sender = _senderFactory.GetSender(topicName);
        //         var message = new ServiceBusMessage(messageBody);

        //         await sender.SendMessageAsync(message, cancellationToken);
        //     }
        // }
    }
}