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

using SimpleL7Proxy.Config;


namespace SimpleL7Proxy.ServiceBus
{

    public class ServiceBusRequestService : IHostedService, IServiceBusRequestService
    {
        private readonly BackendOptions _options;
        private readonly ServiceBusFactory _senderFactory;
        private readonly ILogger<ServiceBusRequestService> _logger;
        public static readonly ConcurrentQueue<ServiceBusStatusMessage> _statusQueue = new ConcurrentQueue<ServiceBusStatusMessage>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isRunning = false;
        private bool isShuttingDown = false;
        private Task? writerTask;
        CancellationTokenSource? _cancellationTokenSource;

        // Batch tuning
        private const int MaxDrainPerCycle = 50; // max messages to drain from queue per cycle
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public ServiceBusRequestService(IOptions<BackendOptions> options, ServiceBusFactory senderFactory, ILogger<ServiceBusRequestService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            _logger = logger;
            
            _logger.LogInformation("ServiceBus feeder tasks configured:");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _logger.LogInformation("[SERVICE] ✓ ServiceBusRequestService starting...");
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource.Token.Register(() =>
                {
                    _logger.LogInformation("[SHUTDOWN] ⏹ ServiceBusRequestService shutdown initiated");
                });

                isRunning = true;

                // Start the writer task but DON'T await it
                writerTask = Task.Run(() => EventWriter(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Return immediately - let the writer task run in the background
            }
            
            return Task.CompletedTask;
        }

        public bool updateStatus(RequestData message)
        {
            try
            {
                _logger.LogDebug($"Enqueuing status message for UserId: {message.MID}, Status: {message.SBStatus} for Topic: {message.SBTopicName}");
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

        public async Task EventWriter(CancellationToken token)
        {

            _logger.LogInformation("[SERVICE] ✓ ServiceBus writer service starting...");

            try
            {
                await Task.Run(() => FeederTask(token), token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit gracefully
                _logger.LogInformation("ServiceBusRequestService task was canceled.");
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, exit gracefully
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogError("ServiceBusRequestService encountered an UnauthorizedAccessException. Check Service Bus connection string and permissions.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while sending a message to the topic.: " + ex);
            }
            finally
            {
                // Flush all items in batches
                var cts = new CancellationTokenSource().Token;

                var drained = new List<ServiceBusStatusMessage>();
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    drained.Add(statusMessage);
                }

                if (drained.Count > 0)
                {
                    var byTopic = GroupByTopic(drained);
                    foreach (var kvp in byTopic)
                    {
                        try
                        {
                            await SendBatchesForTopicAsync(kvp.Key, kvp.Value, cts).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Error while flushing service bus. Continuing.");
                        }
                    }
                }
            }

            _logger.LogInformation("[SHUTDOWN] ✓ ServiceBusRequestService stopped");
        }

        DateTime _lastDrainTime = DateTime.UtcNow;

        private async Task FeederTask(CancellationToken token)
        {
            var drained = new List<ServiceBusStatusMessage>(MaxDrainPerCycle);
            while (!isShuttingDown || !_statusQueue.IsEmpty)
            {
                // don't repeat this loop more than once every FlushIntervalMs unless we are shutting down
                var delta = DateTime.UtcNow - _lastDrainTime;
                if (delta < FlushIntervalMs && !isShuttingDown && !token.IsCancellationRequested)
                {
                    delta = FlushIntervalMs - delta;
                    await Task.Delay(delta, token).ConfigureAwait(false);
                }
                _lastDrainTime = DateTime.UtcNow;

                // Drain all available items before waiting
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    // Process item (e.g., add to batch, send, etc.)
                    drained.Add(statusMessage);
                    if (drained.Count >= MaxDrainPerCycle)
                    {
                        var byTopic = GroupByTopic(drained);

                        foreach (var kvp in byTopic)
                        {
                            try
                            {
                                await SendBatchesForTopicAsync(kvp.Key, kvp.Value, token).ConfigureAwait(false);
                            }
                            catch (ArgumentException ex)
                            {
                                _logger.LogError(ex, $"Error while sending to service bus. Topic: {kvp.Key}. Continuing.");
                            }
                        }
                        drained.Clear();
                    }
                }

                // If any remain after draining, process them
                if (drained.Count > 0)
                {
                    var byTopic = GroupByTopic(drained);

                    foreach (var kvp in byTopic)
                    {
                        await SendBatchesForTopicAsync(kvp.Key, kvp.Value, token).ConfigureAwait(false);
                    }
                    drained.Clear();

                }

                // Now wait for a signal before next round
                while (_statusQueue.IsEmpty && !token.IsCancellationRequested)
                {
                    await _queueSignal.WaitAsync(token).ConfigureAwait(false);
                }
                
                _logger.LogDebug($"Loop: cancelRequest: {token.IsCancellationRequested}, queueCount: {_statusQueue.Count}");
            }
        }

        private static Dictionary<string, List<ServiceBusStatusMessage>> GroupByTopic(List<ServiceBusStatusMessage> items)
        {
            var map = new Dictionary<string, List<ServiceBusStatusMessage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!map.TryGetValue(item.topicName, out var list))
                {
                    list = new List<ServiceBusStatusMessage>();
                    map[item.topicName] = list;
                }
                list.Add(item);
            }
            return map;
        }

        private async Task SendBatchesForTopicAsync(string topicName, List<ServiceBusStatusMessage> items, CancellationToken token)
        {
            var sender = _senderFactory.GetSender(topicName);
            ServiceBusMessageBatch? currentBatch = null;
            try
            {
                currentBatch = await sender.CreateMessageBatchAsync(token).ConfigureAwait(false);

                foreach (var item in items)
                {
                    var message = new ServiceBusMessage(JsonSerializer.Serialize(item));

                    if (!currentBatch.TryAddMessage(message))
                    {
                        // Send the full batch and start a new one
                        await sender.SendMessagesAsync(currentBatch, token).ConfigureAwait(false);
                        currentBatch.Dispose();
                        currentBatch = await sender.CreateMessageBatchAsync(token).ConfigureAwait(false);

                        if (!currentBatch.TryAddMessage(message))
                        {
                            // Single message too large for an empty batch
                            _logger.LogError("Message too large to add to batch for topic {TopicName}. Dropping.", topicName);
                        }
                    }
                }

                if (currentBatch.Count > 0)
                {
                    await sender.SendMessagesAsync(currentBatch, token).ConfigureAwait(false);
                }
            }
            finally
            {
                currentBatch?.Dispose();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            isShuttingDown = true;
            if (isRunning)
            {

                _logger.LogInformation("[SHUTDOWN] ⏳ ServiceBusRequestService flushing {events} events before stopping", _statusQueue.Count);
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
    }
}