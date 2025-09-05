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

namespace API;

public class New
{
    private readonly ILogger<New> _logger;

    public New(ILogger<New> logger)
    {
        _logger = logger;
    }

    [Function("New")]
    [CosmosDBOutput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection", PartitionKey = "/id")]
    public async Task<RequestAPIDocument?> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "new/{requestId}")] HttpRequestData req,
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
                if (requestInput != null)
                {
                    requestInput.id = requestId;
                    requestInput.guid = requestId;

                    _logger.LogInformation("Created new document with ID: {Id}, MID: {Mid} and status: {Status}",
                        requestInput.id, requestInput.mid, requestInput.status);

                    // The output binding will automatically save this to Cosmos DB
                    return requestInput;
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
