using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Messaging;


namespace SimpleL7Proxy.Async.ServiceBus
{

    public class ServiceBusRequestService : IHostedService, IServiceBusRequestService, IBatchMessageTransport<ServiceBusMessageBatch>
    {
        private readonly ProxyConfig _options;
        private readonly IServiceBusFactory _senderFactory;
        private readonly ILogger<ServiceBusRequestService> _logger;
        private readonly IBatchMessageTransport<ServiceBusMessageBatch> _batchTransport;
        private readonly ConcurrentDictionary<string, BatchMessagePump<ServiceBusMessageBatch>> _topicPumps = new(StringComparer.OrdinalIgnoreCase);
        private bool isRunning = false;
        private bool isShuttingDown = false;

        private const int MaxDrainPerCycle = 50;
        private const int FlushCountThreshold = 10;
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromSeconds(2);
        
        // Performance tracking
        private int _totalMessagesProcessed = 0;
        private int _totalBatchesSent = 0;

        public ServiceBusRequestService(IOptions<ProxyConfig> options, IServiceBusFactory senderFactory, ILogger<ServiceBusRequestService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchTransport = this;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _logger.LogInformation("[SERVICE] ✓ ServiceBusRequestService starting...");
                isRunning = true;
            }
            
            return Task.CompletedTask;
        }

        public bool updateStatus(RequestData message)
        {
            if (!isRunning || isShuttingDown)
            {
                return false;
            }

            try
            {
                var topicName = string.IsNullOrWhiteSpace(message.SBTopicName) ? "status" : message.SBTopicName;
                var pump = _topicPumps.GetOrAdd(topicName, static (key, state) =>
                    new BatchMessagePump<ServiceBusMessageBatch>(
                        destination: key,
                        transport: state.Transport,
                        createBatchAsync: cancellationToken => state.Transport.CreateBatchAsync(key, cancellationToken),
                        recoverBatchAsync: cancellationToken => state.Transport.CreateBatchAsync(key, cancellationToken),
                        options: new BatchMessagePumpOptions
                        {
                            MaxBatchItems = MaxDrainPerCycle,
                            FlushCountThreshold = FlushCountThreshold,
                            FlushInterval = FlushIntervalMs,
                            WaitThreshold = state.WaitThreshold,
                            ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
                        }),
                    (Transport: _batchTransport, WaitThreshold: _options.MaxUndrainedEvents / 4));

                pump.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

                _logger.LogDebug("[ServiceBus:{Guid}] Status update enqueued - UserId: {UserId}, Status: {Status}, Topic: {TopicName}, QueueDepth: {QueueCount}", 
                    message.Guid, message.MID, message.SBStatus, topicName, GetQueueDepth() + 1);

                pump.Enqueue(JsonSerializer.Serialize(new ServiceBusStatusMessage(message.Guid, topicName, message.SBStatus.ToString())));

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ServiceBus:{Guid}] Failed to enqueue status update - UserId: {UserId}, Status: {Status}", 
                    message.Guid, message.MID, message.SBStatus);
                return false; // Enqueue failed
            }
        }

        public (int totalMessages, int totalBatches, int queueDepth, bool isEnabled, string? connectionInfo) GetStatistics()
        {
            string? connectionInfo = null;
            
            if (_options.AsyncModeEnabled && _senderFactory != null)
            {
                // Get connection info from the factory (namespace endpoint)
                connectionInfo = _senderFactory.GetConnectionInfo();
            }
            
            return (
                totalMessages: _totalMessagesProcessed,
                totalBatches: _totalBatchesSent,
                queueDepth: GetQueueDepth(),
                isEnabled: _options.AsyncModeEnabled && isRunning,
                connectionInfo: connectionInfo
            );
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (!isRunning)
            {
                return;
            }

            isShuttingDown = true;

            foreach (var pump in _topicPumps.Values)
            {
                pump.BeginShutdown();
            }

            foreach (var kvp in _topicPumps)
            {
                if (kvp.Value.Count > 0)
                {
                    _logger.LogInformation("[SHUTDOWN] ⏳ ServiceBusRequestService - topic {TopicName} has {Events} events to flush", kvp.Key, kvp.Value.Count);
                }
            }

            await Task.WhenAll(_topicPumps.Values.Select(static pump => pump.StopAsync())).ConfigureAwait(false);

            isRunning = false;
            _logger.LogInformation("[SHUTDOWN] ⏹  ServiceBusRequestService stopped");
        }

        Task IBatchMessageTransport<ServiceBusMessageBatch>.OpenAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        ValueTask<ServiceBusMessageBatch> IBatchMessageTransport<ServiceBusMessageBatch>.CreateBatchAsync(string destination, CancellationToken cancellationToken)
        {
            return _senderFactory.GetSender(destination).CreateMessageBatchAsync(cancellationToken);
        }

        bool IBatchMessageTransport<ServiceBusMessageBatch>.TryAdd(ServiceBusMessageBatch batch, BatchMessageEnvelope message)
        {
            var wasEmpty = batch.Count == 0;
            var serviceBusMessage = new ServiceBusMessage(message.Payload);
            var added = batch.TryAddMessage(serviceBusMessage);
            if (!added && wasEmpty)
            {
                _logger.LogError("[ServiceBus:Batch] Message too large for topic {TopicName}. Dropping message.", message.Destination);
            }

            return added;
        }

        int IBatchMessageTransport<ServiceBusMessageBatch>.GetCount(ServiceBusMessageBatch batch)
        {
            return batch.Count;
        }

        async Task IBatchMessageTransport<ServiceBusMessageBatch>.SendAsync(string destination, ServiceBusMessageBatch batch, CancellationToken cancellationToken)
        {
            await _senderFactory.GetSender(destination).SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _totalMessagesProcessed, batch.Count);
            Interlocked.Increment(ref _totalBatchesSent);
            _logger.LogTrace("[ServiceBus:Batch] Sent {MessageCount} messages to topic {TopicName}", batch.Count, destination);
        }

        void IBatchMessageTransport<ServiceBusMessageBatch>.DisposeBatch(ServiceBusMessageBatch batch)
        {
            batch.Dispose();
        }

        Task IBatchMessageTransport<ServiceBusMessageBatch>.CloseAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        private int GetQueueDepth()
        {
            return _topicPumps.Values.Sum(static pump => pump.Count);
        }
    }
}