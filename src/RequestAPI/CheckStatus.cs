using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using Shared.RequestAPI.Models;

namespace RequestAPI;

public class CheckStatus
{
    private readonly ILogger _logger;

    public CheckStatus(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CheckStatus>();
    }

    public class CheckStatusOutput
    {
        [CosmosDBOutput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection")]
        public RequestAPIDocument? UpdatedDocument { get; set; }

        [HttpResult]
        public IActionResult? HttpResponse { get; set; }
    }

    [Function("CheckStatus")]
    public CheckStatusOutput Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "checkStatus/{guid}")] HttpRequest req,
        string guid,
        [CosmosDBInput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection",
            Id = "{guid}",
            PartitionKey = "{guid}")] RequestAPIDocument? document)
    {
        _logger.LogInformation("CheckStatus: Received request for GUID: {guid}", guid);

        if (!Guid.TryParse(guid, out _))
        {
            return new CheckStatusOutput { HttpResponse = new BadRequestObjectResult("Invalid GUID format.") };
        }

        if (document == null)
        {
            return new CheckStatusOutput { HttpResponse = new NotFoundObjectResult($"No document found for GUID: {guid}") };
        }

        document.checkCount++;

        return new CheckStatusOutput
        {
            UpdatedDocument = document,
            HttpResponse = new OkObjectResult(new CheckStatusResponse
            {
                Guid = document.guid ?? string.Empty,
                Status = document.status?.ToString() ?? string.Empty,
                CheckCount = document.checkCount
            })
        };
    }
}
