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

public class NewBulk
{
    private readonly ILogger<NewBulk> _logger;

    public NewBulk(ILogger<NewBulk> logger)
    {
        _logger = logger;
    }

    // NOT USED

    [Function("NewBulk")]
    [CosmosDBOutput("%CosmosDb:DatabaseName%", "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection", PartitionKey = "/id")]
    public async Task<RequestAPIDocument[]> Run(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "bulk/new")] HttpRequestData req)
    {
        _logger.LogInformation("New Bulk Request");

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

            RequestAPIDocument[]? requestInput = JsonSerializer.Deserialize<RequestAPIDocument[]>(requestBody, jsonOptions);
            if (requestInput == null || !requestInput.Any())
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Invalid request format or empty array");
                throw new Exception("Invalid request format or empty array");
            }

            var newDocumentList = requestInput.Select(doc =>
            {
                doc.id = Guid.NewGuid().ToString();
                doc.guid = Guid.NewGuid().ToString();
                return doc;
            }).ToArray();

            _logger.LogInformation("Creating {Count} new documents", newDocumentList.Length);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(newDocumentList);

            return newDocumentList;
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
