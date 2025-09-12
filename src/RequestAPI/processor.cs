using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Shared.RequestAPI.Models;

namespace RequestAPI;

public class DocumentProcessor
{
    private readonly ILogger<DocumentProcessor> _logger;

    public DocumentProcessor(ILogger<DocumentProcessor> logger)
    {
        _logger = logger;
        _logger.LogInformation("DocumentProcessor constructor called. Initializing with settings...");
        
        // Log all environment variables related to Service Bus and Cosmos DB
        var sbQueueName = Environment.GetEnvironmentVariable("ServiceBusQueue");
        var sbConnection = Environment.GetEnvironmentVariable("ServiceBusConnection");
        var sbFullyQualifiedNamespace = Environment.GetEnvironmentVariable("ServiceBusConnection__fullyQualifiedNamespace");
        
        _logger.LogInformation("ServiceBusQueue: {QueueName}", sbQueueName);
        _logger.LogInformation("ServiceBusConnection exists: {ConnectionExists}", !string.IsNullOrEmpty(sbConnection));
        _logger.LogInformation("ServiceBusConnection__fullyQualifiedNamespace: {Namespace}", sbFullyQualifiedNamespace);
        
        var cosmosDbName = Environment.GetEnvironmentVariable("CosmosDb:DatabaseName");
        var cosmosContainerName = Environment.GetEnvironmentVariable("CosmosDb:ContainerName");
        var cosmosConnection = Environment.GetEnvironmentVariable("CosmosDbConnection");
        var cosmosEndpoint = Environment.GetEnvironmentVariable("CosmosDbConnection__accountEndpoint");
        
        _logger.LogInformation("CosmosDb:DatabaseName: {DbName}", cosmosDbName);
        _logger.LogInformation("CosmosDb:ContainerName: {ContainerName}", cosmosContainerName);
        _logger.LogInformation("CosmosDbConnection exists: {ConnectionExists}", !string.IsNullOrEmpty(cosmosConnection));
        _logger.LogInformation("CosmosDbConnection__accountEndpoint: {Endpoint}", cosmosEndpoint);
        
        // Log worker runtime and other diagnostic info
        _logger.LogInformation("FUNCTIONS_WORKER_RUNTIME: {Runtime}", Environment.GetEnvironmentVariable("FUNCTIONS_WORKER_RUNTIME"));
        _logger.LogInformation("Machine name: {MachineName}", Environment.MachineName);
        _logger.LogInformation("OS: {OS}", Environment.OSVersion);
        _logger.LogInformation(".NET version: {Version}", Environment.Version);
    }

    [Function("ProcessDocuments")]
    [CosmosDBOutput(
        databaseName: "%CosmosDb:DatabaseName%",
        containerName: "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection",
        CreateIfNotExists = true)]
    public RequestAPIDocument[] Run(
        [ServiceBusTrigger("%ServiceBusQueue%", Connection = "ServiceBusConnection")] string messageBody,
        [CosmosDBInput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection",
            Id = "{id}",
            PartitionKey = "{id}")]
        RequestAPIDocument existingDocument)
    {
        _logger.LogInformation("ProcessDocuments Function triggered with message: {MessageLength} chars", messageBody?.Length ?? 0);
        _logger.LogTrace("Message body: {MessageBody}", messageBody);
        
        var NullDocuments = Array.Empty<RequestAPIDocument>();
        
        _logger.LogTrace("Function invocation details: {@Details}", new 
        { 
            FunctionName = "ProcessDocuments",
            StartTime = DateTime.UtcNow,
            MessageBody = messageBody
        });

        try
        {
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Converters = { new CaseInsensitiveEnumConverter<RequestAPIStatusEnum>() }
            };

            var document = JsonSerializer.Deserialize<RequestAPIDocument>(messageBody, jsonOptions);

            if (document == null)
            {
                _logger.LogError("Invalid message format or empty document");
                return NullDocuments;
            }

            RequestAPIDocument[] documents;
            bool isNew = document.status == RequestAPIStatusEnum.New;

            if (isNew)
            {
                // Process as new document
                document.id = document.id ?? Guid.NewGuid().ToString();
                document.guid = document.guid ?? document.id;
                document.mid = document.mid ?? document.id;
                document.createdAt = document.createdAt ?? DateTime.UtcNow;
                document.isAsync = document.isAsync ?? false;
                document.isBackground = document.isBackground ?? false;
                document.userID = document.userID ?? "system";
                document.priority1 = document.priority1 ?? 1;
                document.priority2 = document.priority2 ?? 1;
                document.status = RequestAPIStatusEnum.New;
                documents = new[] { document };
            }
            else
            {
                // Process as update
                if (string.IsNullOrEmpty(document.id))
                {
                    _logger.LogWarning("No valid ID found in the update request");
                    return NullDocuments;
                }

                if (existingDocument == null)
                {
                    _logger.LogWarning("No existing document found with ID: {Id}", document.id);
                    return NullDocuments;
                }

                // Update the existing document with new values, preserving existing ones if not specified
                document.guid = document.guid ?? existingDocument.guid;
                document.mid = document.mid ?? existingDocument.mid;
                document.createdAt = existingDocument.createdAt; // Preserve original creation time
                document.isAsync = document.isAsync ?? existingDocument.isAsync;
                document.isBackground = document.isBackground ?? existingDocument.isBackground;
                document.userID = document.userID ?? existingDocument.userID;
                document.priority1 = document.priority1 ?? existingDocument.priority1;
                document.priority2 = document.priority2 ?? existingDocument.priority2;
                document.status = document.status ?? existingDocument.status;
                // document.status = document.status != 0 ? document.status : existingDocument.status; // Preserve status if not provided

                documents = new[] { document };
            }

            _logger.LogInformation("Processed document with ID: {Id}", document.id);
            return documents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing document: {Message}", ex.Message);
            return NullDocuments;
        }
    }
}
