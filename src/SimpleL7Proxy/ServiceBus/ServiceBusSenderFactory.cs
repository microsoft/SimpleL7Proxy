using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;

namespace SimpleL7Proxy.ServiceBus
{
    public class ServiceBusSenderFactory
    {
        private readonly IOptionsMonitor<BackendOptions> _optionsMonitor;

        private readonly ServiceBusClient _client = null!;
        private readonly Dictionary<string, ServiceBusSender> _senders =null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceBusSenderFactory"/> class.
        /// </summary>
        /// <param name="client">The ServiceBusClient instance provided by DI.</param>
        public ServiceBusSenderFactory(IOptionsMonitor<BackendOptions> optionsMonitor)
        {
            _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
            if (optionsMonitor.CurrentValue.AsyncModeEnabled)
            {
                _client = new ServiceBusClient(optionsMonitor.CurrentValue.AsyncBlobStorageConnectionString);
                _senders = new Dictionary<string, ServiceBusSender>();
            }
        }

        /// <summary>
        /// Gets or creates a ServiceBusSender for the specified topic.
        /// </summary>
        /// <param name="topicName">The name of the topic.</param>
        /// <returns>A ServiceBusSender instance.</returns>
        public ServiceBusSender GetSender(string topicName)
        {
            if (string.IsNullOrWhiteSpace(topicName))
            {
                throw new ArgumentException("Topic name cannot be null or empty.", nameof(topicName));
            }

            if (!_senders.ContainsKey(topicName))
            {
                _senders[topicName] = _client.CreateSender(topicName);
            }
            return _senders[topicName];
        }
    }
}