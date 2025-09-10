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
    }

    [Function("ProcessDocuments")]
    [CosmosDBOutput(
        databaseName: "%CosmosDb:DatabaseName%",
        containerName: "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection",
        CreateIfNotExists = true)]
    public RequestAPIDocument[] Run(
        [ServiceBusTrigger("requeststatus", Connection = "ServiceBusConnection")] string messageBody,
        [CosmosDBInput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection",
            Id = "{id}",
            PartitionKey = "{id}")]
        RequestAPIDocument existingDocument)
    {
        _logger.LogInformation("Processing Document Operation Started");
        
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
