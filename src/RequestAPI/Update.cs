using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Shared.RequestAPI.Models;


namespace RequestAPI;

public class Update
{
    private readonly ILogger<Update> _logger;

    public Update(ILogger<Update> logger)
    {
        _logger = logger;
    }

    [Function("Update")]
    [CosmosDBOutput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection", PartitionKey = "/id")]
    public async Task<RequestAPIDocument?> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "update/{requestId}")] HttpRequestData req,
        [CosmosDBInput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection", Id = "{requestId}", PartitionKey = "{requestId}")] RequestAPIDocument? existingDocument,
            string requestId)
    {
        _logger.LogInformation($"New Request - {requestId}");
        // parse inbound request
        string requestBody;
        using (var reader = new StreamReader(req.Body))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {

                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    Converters = { new CaseInsensitiveEnumConverter<RequestAPIStatusEnum>() }
                };

                RequestAPIDocument? requestInput = JsonSerializer.Deserialize<RequestAPIDocument>(requestBody, jsonOptions);
                var newDocument = new RequestAPIDocument();
                if (requestInput != null)
                {
                    newDocument.status = requestInput.status ?? existingDocument.status;
                    newDocument.guid = requestId;
                    newDocument.id = existingDocument.id;
                    newDocument.mid = requestInput.mid ?? existingDocument.mid;
                    newDocument.isAsync = requestInput.isAsync ?? existingDocument.isAsync;
                    newDocument.isBackground = requestInput.isBackground ?? existingDocument.isBackground;
                    newDocument.createdAt = requestInput.createdAt ?? existingDocument.createdAt;
                    newDocument.userID = requestInput.userID ?? existingDocument.userID;
                    newDocument.priority1 = requestInput.priority1 ?? existingDocument.priority1;
                    newDocument.priority2 = requestInput.priority2 ?? existingDocument.priority2;

                    _logger.LogInformation("Updating document with ID: {Id}, MID: {Mid} and status: {Status}",
                        newDocument.id, newDocument.mid, newDocument.status);

                    // The output binding will automatically save this to Cosmos DB
                    return newDocument;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse request body");
                throw new ArgumentException("Invalid JSON in request body", ex);
            }
        }

        return null;

    }

}
