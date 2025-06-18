using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.ServiceBus
{
    public class ServiceBusSenderFactory
    {
        private readonly IOptionsMonitor<BackendOptions> _optionsMonitor;
        private readonly ILogger<ServiceBusSenderFactory> _logger;

        private readonly ServiceBusClient _client = null!;
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceBusSenderFactory"/> class.
        /// This factory manages ServiceBus senders for different topics in async processing mode.
        /// </summary>
        /// <param name="optionsMonitor">The options monitor providing access to the backend configuration settings.</param>
        /// <param name="logger">The logger for recording factory operations and diagnostics.</param>
        /// <remarks>
        /// The factory initializes the ServiceBusClient only when AsyncModeEnabled is true in the backend options.
        /// This prevents unnecessary connection creation when async processing is not required.
        /// </remarks>
        public ServiceBusSenderFactory(IOptionsMonitor<BackendOptions> optionsMonitor, ILogger<ServiceBusSenderFactory> logger)
        {
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                _client = new ServiceBusClient(optionsMonitor.CurrentValue.AsyncSBConnectionString);
                _senders = new();
            }
        }

        /// <summary>
        /// Gets or creates a ServiceBusSender for the specified topic.
        /// </summary>
        /// <param name="topicName">The name of the topic to send messages to.</param>
        /// <returns>A ServiceBusSender instance configured for the specified topic.</returns>
        /// <exception cref="ArgumentException">Thrown when the topic name is null or empty.</exception>
        /// <remarks>
        /// This method implements a thread-safe lazy initialization pattern for ServiceBusSender instances.
        /// It reuses existing senders when available, creating new ones only when needed, which
        /// improves performance and reduces resource consumption by maintaining a cache of senders.
        /// </remarks>
        public ServiceBusSender GetSender(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
            {
                throw new ArgumentException("Topic name cannot be null or empty.", nameof(topicName));
            }

            if (!_senders.ContainsKey(topicName))
            {
                _logger.LogInformation($"Creating new ServiceBusSender for topic: {topicName}");
                _senders[topicName] = _client.CreateSender(topicName);
            }
            return _senders[topicName];
        }
    }
}