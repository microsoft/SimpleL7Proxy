using System;
using System.Collections.Generic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Shared.RequestAPI.Models;

namespace RequestAPI;

/// <summary>
/// Azure Function that runs every minute to resubmit requests that need reprocessing.
/// Queries Cosmos DB for documents with status = NeedsReprocessing (4), updates them to ReSubmitted (5),
/// and sends them back to the Service Bus queue for reprocessing.
/// </summary>

public class ReFeeder
{
    private readonly ILogger _logger;

    public ReFeeder(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ReFeeder>();
    }

    [Function("ReFeeder")]
    public OutputData Run(
        [TimerTrigger("0 */1 * * * *")] TimerInfo myTimer,
        [CosmosDBInput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection",
            SqlQuery = "SELECT * FROM c WHERE c.status = 4")] IEnumerable<RequestAPIDocument> pendingDocuments)
    {

        if (myTimer.ScheduleStatus is not null)
        {
            _logger.LogInformation("ReFeeder:   Next timer schedule at: {nextSchedule}", myTimer.ScheduleStatus.Next);
        }

        if (pendingDocuments == null || !pendingDocuments.Any())
        {
            _logger.LogInformation("ReFeeder:   No pending documents found that need processing.");


            return new OutputData(); // Return empty output if no documents to process
        }

        OutputData output = new OutputData();

        List<RequestAPIDocument> outputMessages = new List<RequestAPIDocument>();

        foreach (var document in pendingDocuments)
        {
            _logger.LogInformation("ReFeeder:   Found pending document with ID: {id}, created at: {createdAt}",
                document.id, document.createdAt);

            // Process the document here or send it to a queue for processing
            document.status = RequestAPIStatusEnum.ReSubmitted;
            outputMessages.Add(document);
        }

        output.DBMessages = outputMessages.ToArray();
        output.QMessages = outputMessages.ToArray();

        _logger.LogInformation("ReFeeder:   Retrieved {count} pending documents that need processing", outputMessages.Count);
        return output;
    }

}