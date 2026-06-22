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


namespace SimpleL7Proxy.Async.ServiceBus.SBTopic
{

    public class SBTopicService : IHostedService, ISBTopicService, IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>,
        IReadinessParticipant
    {
        public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.SBTopic;
        public ReadinessRegistry Readiness { get; }

        private readonly ProxyConfig _options;
        private readonly IServiceBusFactory _senderFactory;
        private readonly ILogger<SBTopicService> _logger;
        private readonly IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope> _batchTransport;
        private readonly ConcurrentDictionary<string, BatchMessagePump<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>> _topicPumps = new(StringComparer.OrdinalIgnoreCase);
        private bool isRunning = false;
        private bool isShuttingDown = false;

        private const int MaxDrainPerCycle = 50;
        private const int FlushCountThreshold = 10;
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromSeconds(2);

        // Throttled "Sent N messages" log aggregation, keyed per topic.
        // Trace -> log every batch, Debug -> 10s, Info -> 60s, below Info -> silent.
        private static readonly TimeSpan SendLogIntervalDebug = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SendLogIntervalInfo = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SendLogIntervalShutdown = TimeSpan.FromSeconds(1);
        private sealed class SendLogAgg
        {
            public int Batches;
            public int Messages;
            public DateTime WindowStart = DateTime.UtcNow;
        }
        private readonly ConcurrentDictionary<string, SendLogAgg> _sendLogAgg = new(StringComparer.OrdinalIgnoreCase);

        // Performance tracking
        private int _totalMessagesProcessed = 0;
        private int _totalBatchesSent = 0;

        public SBTopicService(IOptions<ProxyConfig> options, IServiceBusFactory senderFactory, ReadinessRegistry readiness, ILogger<SBTopicService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchTransport = this;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _logger.LogInformation("[SERVICE] ✓ SBTopicService starting...");
                isRunning = true;
                this.RegisterReady();
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
                    new BatchMessagePump<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>(
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

                pump.Enqueue(new BinaryBatchMessageEnvelope(topicName, JsonSerializer.SerializeToUtf8Bytes(new ServiceBusStatusMessage(message.Guid, topicName, message.SBStatus.ToString()))));

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ServiceBus:{Guid}] Failed to enqueue status update - UserId: {UserId}, Status: {Status}, Topic: {Topic}, Error: {Error}",
                    message.Guid, message.MID, message.SBStatus, message.SBTopicName, ex.Message);
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
                    _logger.LogInformation("[SHUTDOWN] ⏳ SBTopicService - topic {TopicName} has {Events} events to flush", kvp.Key, kvp.Value.Count);
                }
            }

            await Task.WhenAll(_topicPumps.Values.Select(static pump => pump.StopAsync())).ConfigureAwait(false);

            isRunning = false;
            _logger.LogInformation("[SHUTDOWN] ⏹  SBTopicService stopped");
        }

        Task IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.OpenAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        ValueTask<ServiceBusMessageBatch> IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.CreateBatchAsync(string destination, CancellationToken cancellationToken)
        {
            return _senderFactory.GetSender(destination).CreateMessageBatchAsync(cancellationToken);
        }

        bool IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.TryAdd(ServiceBusMessageBatch batch, BinaryBatchMessageEnvelope message)
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

        int IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.GetCount(ServiceBusMessageBatch batch)
        {
            return batch.Count;
        }

        async Task IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.SendAsync(string destination, ServiceBusMessageBatch batch, CancellationToken cancellationToken)
        {
            var count = batch.Count;
            await _senderFactory.GetSender(destination).SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _totalMessagesProcessed, count);
            Interlocked.Increment(ref _totalBatchesSent);
            LogSendThrottled(destination, count);
        }

        private void LogSendThrottled(string destination, int count)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[ServiceBus:Batch] Sent {MessageCount} messages to topic {TopicName}", count, destination);
                return;
            }

            var debugOn = _logger.IsEnabled(LogLevel.Debug);
            var infoOn  = _logger.IsEnabled(LogLevel.Information);
            if (!debugOn && !infoOn)
            {
                return;
            }

            var interval = debugOn ? SendLogIntervalDebug : SendLogIntervalInfo;
            if (isShuttingDown)
            {
                interval = SendLogIntervalShutdown;
            }
            var agg = _sendLogAgg.GetOrAdd(destination, static _ => new SendLogAgg());

            int totalBatches;
            int totalMessages;
            double elapsedSeconds;
            lock (agg)
            {
                agg.Batches++;
                agg.Messages += count;
                var elapsed = DateTime.UtcNow - agg.WindowStart;
                if (elapsed < interval)
                {
                    return;
                }
                totalBatches = agg.Batches;
                totalMessages = agg.Messages;
                elapsedSeconds = elapsed.TotalSeconds;
                agg.Batches = 0;
                agg.Messages = 0;
                agg.WindowStart = DateTime.UtcNow;
            }

            if (debugOn)
            {
                _logger.LogDebug("[ServiceBus:Batch] Sent {MessageCount} messages in {BatchCount} batches to topic {TopicName} over {Elapsed:F1}s",
                    totalMessages, totalBatches, destination, elapsedSeconds);
            }
            else
            {
                _logger.LogInformation("[ServiceBus:Batch] Sent {MessageCount} messages in {BatchCount} batches to topic {TopicName} over {Elapsed:F1}s",
                    totalMessages, totalBatches, destination, elapsedSeconds);
            }
        }

        void IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.DisposeBatch(ServiceBusMessageBatch batch)
        {
            batch.Dispose();
        }

        Task IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.CloseAsync(CancellationToken cancellationToken)
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