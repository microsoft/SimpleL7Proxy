using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;

namespace SimpleL7Proxy.ServiceBus
{
    public class ServiceBusSenderFactory
    {
        private readonly ServiceBusClient _client;
        private readonly Dictionary<string, ServiceBusSender> _senders;

        /// <summary>
        /// Initializes a new instance of the <see cref="ServiceBusSenderFactory"/> class.
        /// </summary>
        /// <param name="client">The ServiceBusClient instance provided by DI.</param>
        public ServiceBusSenderFactory(ServiceBusClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _senders = new Dictionary<string, ServiceBusSender>();
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