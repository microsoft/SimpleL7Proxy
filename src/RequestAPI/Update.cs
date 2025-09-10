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

    // NOT USED

    [Function("Update")]
    [CosmosDBOutput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
    Connection = "CosmosDbConnection", PartitionKey = "{requestId}")]
    public async Task<RequestAPIDocument?> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "update/{requestId}")] HttpRequestData req,
    [CosmosDBInput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection", Id = "{requestId}", PartitionKey = "{requestId}")] RequestAPIDocument? existingDocument,
    string requestId)
    {
        _logger.LogInformation($"Update Request - {requestId}");

        if (existingDocument == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Document with ID {requestId} not found");
            throw new Exception($"Document with ID {requestId} not found");
        }

        string requestBody;
        using (var reader = new StreamReader(req.Body))
        {
            requestBody = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(requestBody))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Request body cannot be empty");
            throw new Exception("Request body cannot be empty");
        }

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

            RequestAPIDocument? requestInput = JsonSerializer.Deserialize<RequestAPIDocument>(requestBody, jsonOptions);
            if (requestInput == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Invalid request format");
                throw new Exception("Invalid request format");
            }

            var newDocument = new RequestAPIDocument
            {
                status = requestInput.status ?? existingDocument.status,
                guid = requestId,
                id = existingDocument.id,
                mid = requestInput.mid ?? existingDocument.mid,
                isAsync = requestInput.isAsync ?? existingDocument.isAsync,
                isBackground = requestInput.isBackground ?? existingDocument.isBackground,
                createdAt = requestInput.createdAt ?? existingDocument.createdAt,
                userID = requestInput.userID ?? existingDocument.userID,
                priority1 = requestInput.priority1 ?? existingDocument.priority1,
                priority2 = requestInput.priority2 ?? existingDocument.priority2
            };

            _logger.LogInformation("Updating document with ID: {Id}, MID: {Mid} and status: {Status}",
                newDocument.id, newDocument.mid, newDocument.status);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(newDocument);

            return newDocument;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse request body");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid JSON in request body");
            throw new Exception("Invalid JSON in request body", ex);
        }
    }
}