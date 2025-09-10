using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
            var options = optionsMonitor.CurrentValue;
            try
            {
                if (options.AsyncModeEnabled)
                {
                    _senders = new();

                    if (options.AsyncSBUseMI)
                    {
                        var fullyQualifiedNamespace = options.AsyncSBNamespace;
                        if (string.IsNullOrWhiteSpace(fullyQualifiedNamespace) ||
                            !fullyQualifiedNamespace.EndsWith(".servicebus.windows.net", StringComparison.OrdinalIgnoreCase))
                        {
                            options.AsyncModeEnabled = false; // Disable async mode if namespace is not set
                            _logger.LogCritical("Async mode disabled due to missing or invalid AsyncSBNamespace configuration.");
                        }

                        _client = CreateServiceBusClientWithManagedIdentity(fullyQualifiedNamespace);
                    }
                    else
                    {
                        var connectionString = options.AsyncSBConnectionString;
                        if (string.IsNullOrWhiteSpace(connectionString) ||
                            !connectionString.Contains("Endpoint=", StringComparison.OrdinalIgnoreCase))
                        {
                            options.AsyncModeEnabled = false; // Disable async mode if connection string is not set
                            _logger.LogCritical("Async mode disabled due to missing or invalid AsyncSBConnectionString configuration.");
                        }
                        else
                        {
                            _client = CreateServiceBusClientWithConnectionString(connectionString);
                        }
                    }
                }
            } catch (Exception ex)
            {
                _logger!.LogError(ex, "Failed to initialize ServiceBusSenderFactory");
                options.AsyncModeEnabled = false; // Disable async mode if initialization fails
                _logger.LogCritical("Async mode disabled due to initialization failure.");
            }
        }

        /// <summary>
        /// Creates a ServiceBusClient using connection string authentication.
        /// </summary>
        /// <param name="connectionString">The Service Bus connection string with send permissions.</param>
        /// <returns>A ServiceBusClient instance configured with connection string authentication.</returns>
        /// <exception cref="ArgumentException">Thrown when the connection string is null or empty.</exception>
        /// <exception cref="Exception">Thrown when ServiceBusClient creation fails.</exception>
        /// <remarks>
        /// This method is used for development environments or when using Service Bus access keys.
        /// The connection string must have permissions to send messages to the configured topics.
        /// </remarks>
        private ServiceBusClient CreateServiceBusClientWithConnectionString(string connectionString)
        {
            try
            {
                _logger.LogInformation("Creating ServiceBusClient with connection string authentication.");
                return new ServiceBusClient(connectionString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ServiceBusClient with connection string: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Creates a ServiceBusClient using managed identity authentication.
        /// </summary>
        /// <param name="fullyQualifiedNamespace">The fully qualified namespace of the Service Bus (e.g., myservicebus.servicebus.windows.net).</param>
        /// <returns>A ServiceBusClient instance configured with managed identity authentication.</returns>
        /// <exception cref="ArgumentException">Thrown when the namespace is null or empty.</exception>
        /// <exception cref="Exception">Thrown when ServiceBusClient creation fails.</exception>
        /// <remarks>
        /// This method is recommended for production environments as it provides enhanced security.
        /// The managed identity must be granted the "Azure Service Bus Data Sender" role on the Service Bus namespace.
        /// Uses DefaultAzureCredential which automatically handles different credential types in the following order:
        /// - Environment credentials, Managed Identity, Visual Studio, Azure CLI, etc.
        /// </remarks>
        private ServiceBusClient CreateServiceBusClientWithManagedIdentity(string fullyQualifiedNamespace)
        {
            try
            {
                _logger.LogInformation("Creating ServiceBusClient with managed identity for namespace: {Namespace}", fullyQualifiedNamespace);
                
                // Use DefaultAzureCredential for managed identity authentication
                var credential = new DefaultAzureCredential();
                return new ServiceBusClient(fullyQualifiedNamespace, credential);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ServiceBusClient with managed identity: {Message}", ex.Message);
                throw;
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

        public ServiceBusSender GetQueueSender(string queueName)
        {
            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new ArgumentException("Queue name cannot be null or empty.", nameof(queueName));
            }

            var sender = _client.CreateSender(queueName);
            return sender;
        }
    }
}