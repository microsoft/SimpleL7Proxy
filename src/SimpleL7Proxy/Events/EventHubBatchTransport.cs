using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;
using System.Text;

using SimpleL7Proxy.Config;
using SimpleL7Proxy.Messaging;

namespace SimpleL7Proxy.Events;

internal sealed class EventHubBatchTransport : IBatchMessageTransport<EventDataBatch>
{
    private readonly EventHubConfig _config;
    private readonly DefaultCredential _defaultCredential;
    private readonly ILogger _logger;
    private EventHubProducerClient? _producerClient;

    public EventHubBatchTransport(EventHubConfig config, DefaultCredential defaultCredential, ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _defaultCredential = defaultCredential ?? throw new ArgumentNullException(nameof(defaultCredential));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_producerClient is not null)
        {
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(_config.ConnectionString))
        {
            _logger.LogInformation("[EVENT HUB] connecting via connection string, eventhubname :{EventHubName}", _config.EventHubName);
            _producerClient = new EventHubProducerClient(_config.ConnectionString, _config.EventHubName);
            return Task.CompletedTask;
        }

        if (!string.IsNullOrEmpty(_config.EventHubNamespace))
        {
            var credential = _defaultCredential.Credential;
            var fullyQualifiedNamespace = _config.EventHubNamespace;
            if (!fullyQualifiedNamespace.EndsWith(".servicebus.windows.net") &&
                !fullyQualifiedNamespace.EndsWith(".servicebus.usgovcloudapi.net"))
            {
                fullyQualifiedNamespace = $"{_config.EventHubNamespace}.servicebus.windows.net";
            }

            _producerClient = new EventHubProducerClient(fullyQualifiedNamespace, _config.EventHubName, credential);
            return Task.CompletedTask;
        }

        throw new InvalidOperationException("Event Hub connection details are not configured.");
    }

    public ValueTask<EventDataBatch> CreateBatchAsync(string destination, CancellationToken cancellationToken)
    {
        if (_producerClient is not { } producerClient)
        {
            throw new InvalidOperationException("Event Hub producer is not initialized.");
        }

        return producerClient.CreateBatchAsync(cancellationToken);
    }

    public bool TryAdd(EventDataBatch batch, BatchMessageEnvelope message)
    {
        return batch.TryAdd(new EventData(Encoding.UTF8.GetBytes(message.Payload)));
    }

    public int GetCount(EventDataBatch batch)
    {
        return batch.Count;
    }

    public Task SendAsync(string destination, EventDataBatch batch, CancellationToken cancellationToken)
    {
        if (_producerClient is not { } producerClient)
        {
            throw new InvalidOperationException("Event Hub producer is not initialized.");
        }

        return producerClient.SendAsync(batch, cancellationToken);
    }

    public void DisposeBatch(EventDataBatch batch)
    {
        batch.Dispose();
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_producerClient is null)
        {
            return;
        }

        var producerClient = _producerClient;
        _producerClient = null;
        await producerClient.CloseAsync(cancellationToken).ConfigureAwait(false);
    }
}