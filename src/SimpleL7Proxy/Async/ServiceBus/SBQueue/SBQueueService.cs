using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Messaging;
using Shared.RequestAPI.Models;
using SimpleL7Proxy.Async.ServiceBus;


namespace SimpleL7Proxy.Async.ServiceBus.SBQueue
{
    public class SBQueueService : IHostedService, ISBQueueService, IShutdownParticipant,
        IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>,
        IReadinessParticipant
    {
        public int ShutdownOrder => 20;

        public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.SBQueue;
        public ReadinessRegistry Readiness { get; }

        private readonly ProxyConfig _options;
        private readonly ILogger<SBQueueService> _logger;
        private readonly IServiceBusFactory _senderFactory;
        private readonly IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope> _batchTransport;

        private BatchMessagePump<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>? _pump;
        private bool _isRunning;
        private bool _isShuttingDown;

        // Statistics tracking - events sent / errors per minute for the last 10 minutes.
        // Updated only by the pump's single writer task (via SendAsync), so no locking needed.
        private readonly List<(DateTime Timestamp, int Count)> _minuteStats = new(10);
        private readonly List<(DateTime Timestamp, int Count)> _minuteErrors = new(10);
        private int _currentMinuteCount;
        private int _currentMinuteErrors;
        private DateTime _currentMinuteStart = DateTime.UtcNow;

        // Batch tuning - mirrors SBTopicService.
        private const int MaxDrainPerCycle = 50;
        private const int FlushCountThreshold = 10;
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromSeconds(1);

        // Throttled "Sent N status updates" log aggregation.
        // Trace  -> log every batch
        // Debug  -> aggregate, emit every 10s
        // Info   -> aggregate, emit every 60s
        // Accessed only from the pump's single writer task (SendAsync), so no locking.
        private static readonly TimeSpan SendLogIntervalDebug = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SendLogIntervalInfo  = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan SendLogIntervalShutdown = TimeSpan.FromSeconds(1);
        private int _aggBatches;
        private int _aggMessages;
        private DateTime _aggWindowStart = DateTime.UtcNow;

        public SBQueueService(IOptions<ProxyConfig> options, IServiceBusFactory senderFactory, ReadinessRegistry readiness, ILogger<SBQueueService> logger)
        {
            _options = options.Value;
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            Readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _batchTransport = this;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_options.AsyncModeEnabled)
            {
                return Task.CompletedTask;
            }

            _logger.LogInformation("[SERVICE] ✓ SBQueue service starting...");

            var queueName = _options.AsyncSBQueue;
            _pump = new BatchMessagePump<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>(
                destination: queueName,
                transport: _batchTransport,
                createBatchAsync: ct => _batchTransport.CreateBatchAsync(queueName, ct),
                recoverBatchAsync: ct => _batchTransport.CreateBatchAsync(queueName, ct),
                options: new BatchMessagePumpOptions
                {
                    MaxBatchItems = MaxDrainPerCycle,
                    FlushCountThreshold = FlushCountThreshold,
                    FlushInterval = FlushIntervalMs,
                    WaitThreshold = Math.Max(1, _options.MaxUndrainedEvents / 4),
                    ShutdownDrainTimeout = TimeSpan.FromSeconds(60),
                });

            _isRunning = true;
            var startTask = _pump.StartAsync(cancellationToken);
            this.RegisterReady();
            return startTask;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            return StopAsync(cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;

            if (!_isRunning || _isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            _logger.LogInformation("[SHUTDOWN] SBQueueService stopping...");

            if (_pump != null)
            {
                _pump.BeginShutdown();
                if (_pump.Count > 0)
                {
                    _logger.LogInformation("[SHUTDOWN] ⏳ SBQueueService - queue {QueueName} has {Events} events to flush",
                        _options.AsyncSBQueue, _pump.Count);
                }

                await _pump.StopAsync().ConfigureAwait(false);
            }

            _isRunning = false;
            _logger.LogInformation("[SHUTDOWN] ⏹  SBQueueService stopped");
        }

        public bool UpdateStatus(RequestAPIDocument message)
        {
            if (!_isRunning || _isShuttingDown || _pump == null)
            {
                return false;
            }

            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(message, jsonOptions);
                _pump.Enqueue(new BinaryBatchMessageEnvelope(_options.AsyncSBQueue, payload));

                _logger.LogDebug("[SBQueue:{Guid}] Status update enqueued - UserId: {UserId}, Status: {Status}, QueueDepth: {QueueCount}",
                    message.guid, message.userID, message.status, _pump.Count);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SBQueue:{Guid}] Failed to enqueue status update - UserId: {UserId}",
                    message.guid, message.userID);
                return false;
            }
        }

        /// <summary>
        /// Gets statistics for events sent in the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is event count.
        /// </summary>
        public Dictionary<int, int> GetEventStatistics()
        {
            return BuildMinuteSnapshot(_minuteStats, _currentMinuteCount);
        }

        /// <summary>
        /// Gets error statistics for the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is error count.
        /// </summary>
        public Dictionary<int, int> GetErrorStatistics()
        {
            return BuildMinuteSnapshot(_minuteErrors, _currentMinuteErrors);
        }

        private static Dictionary<int, int> BuildMinuteSnapshot(List<(DateTime Timestamp, int Count)> history, int currentMinute)
        {
            var stats = new Dictionary<int, int>();
            var now = DateTime.UtcNow;

            for (int i = 0; i < 10; i++)
            {
                stats[i] = 0;
            }

            for (int i = 0; i < history.Count; i++)
            {
                var (timestamp, count) = history[i];
                var minutesAgo = (int)(now - timestamp).TotalMinutes;
                if (minutesAgo >= 0 && minutesAgo < 10)
                {
                    stats[minutesAgo] = count;
                }
            }

            stats[0] = currentMinute;
            return stats;
        }

        private void RotateMinuteIfNeeded()
        {
            var now = DateTime.UtcNow;
            var minutesSinceStart = (now - _currentMinuteStart).TotalMinutes;

            if (minutesSinceStart < 1.0)
            {
                return;
            }

            if (_currentMinuteCount > 0)
            {
                _minuteStats.Add((_currentMinuteStart, _currentMinuteCount));
            }

            if (_currentMinuteErrors > 0)
            {
                _minuteErrors.Add((_currentMinuteStart, _currentMinuteErrors));
            }

            _minuteStats.RemoveAll(s => (now - s.Timestamp).TotalMinutes >= 10);
            _minuteErrors.RemoveAll(e => (now - e.Timestamp).TotalMinutes >= 10);

            _currentMinuteStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
            _currentMinuteCount = 0;
            _currentMinuteErrors = 0;
        }

        private void LogSendThrottled(string destination, int count)
        {
            // Trace: log every batch verbatim.
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[SBQueue:Batch] Sent {MessageCount} status updates to queue {QueueName}", count, destination);
                return;
            }

            _aggBatches++;
            _aggMessages += count;

            var debugOn = _logger.IsEnabled(LogLevel.Debug);
            var infoOn  = _logger.IsEnabled(LogLevel.Information);
            if (!debugOn && !infoOn)
            {
                return;
            }

            var interval = debugOn ? SendLogIntervalDebug : SendLogIntervalInfo;
            if (_isShuttingDown)
            {
                interval = SendLogIntervalShutdown;
            }
            var elapsed  = DateTime.UtcNow - _aggWindowStart;
            if (elapsed < interval)
            {
                return;
            }

            if (debugOn)
            {
                _logger.LogDebug("[SBQueue:Batch] Sent {MessageCount} status updates in {BatchCount} batches to queue {QueueName} over {Elapsed:F1}s",
                    _aggMessages, _aggBatches, destination, elapsed.TotalSeconds);
            }
            else
            {
                _logger.LogInformation("[SBQueue:Batch] Sent {MessageCount} status updates in {BatchCount} batches to queue {QueueName} over {Elapsed:F1}s",
                    _aggMessages, _aggBatches, destination, elapsed.TotalSeconds);
            }

            _aggBatches = 0;
            _aggMessages = 0;
            _aggWindowStart = DateTime.UtcNow;
        }

        static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new CaseInsensitiveEnumConverter<RequestAPIStatusEnum>() }
        };

        // ---- IBatchMessageTransport implementation ----

        Task IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.OpenAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.CompletedTask;
        }

        ValueTask<ServiceBusMessageBatch> IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.CreateBatchAsync(string destination, CancellationToken cancellationToken)
        {
            return _senderFactory.GetQueueSender(destination).CreateMessageBatchAsync(cancellationToken);
        }

        bool IBatchMessageTransport<ServiceBusMessageBatch, BinaryBatchMessageEnvelope>.TryAdd(ServiceBusMessageBatch batch, BinaryBatchMessageEnvelope message)
        {
            var wasEmpty = batch.Count == 0;
            var serviceBusMessage = new ServiceBusMessage(message.Payload);
            var added = batch.TryAddMessage(serviceBusMessage);
            if (!added && wasEmpty)
            {
                _logger.LogError("[SBQueue:Batch] Message too large for queue {QueueName}. Dropping message.", message.Destination);
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
            try
            {
                await _senderFactory.GetQueueSender(destination).SendMessagesAsync(batch, cancellationToken).ConfigureAwait(false);
                RotateMinuteIfNeeded();
                _currentMinuteCount += count;
                LogSendThrottled(destination, count);
            }
            catch
            {
                RotateMinuteIfNeeded();
                _currentMinuteErrors += count;
                throw;
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
    }
}
