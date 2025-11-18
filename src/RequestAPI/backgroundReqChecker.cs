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

public class backgroundReqChecker
{
    private readonly ILogger _logger;

    public backgroundReqChecker(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<backgroundReqChecker>();
    }

    [Function("backgroundReqChecker")]
    public OutputData Run(
        [TimerTrigger("0 */1 * * * *")] TimerInfo myTimer,
        [CosmosDBInput(
            databaseName: "%CosmosDb:DatabaseName%",
            containerName: "%CosmosDb:ContainerName%",
            Connection = "CosmosDbConnection",
            SqlQuery = "SELECT * FROM c WHERE c.isBackground = true and c.status = 7")] IEnumerable<RequestAPIDocument> pendingDocuments)
    {

        if (myTimer.ScheduleStatus is not null)
        {
            _logger.LogDebug("backgroundReqChecker:   Next timer schedule at: {nextSchedule}", myTimer.ScheduleStatus.Next);
        }

        if (pendingDocuments == null || !pendingDocuments.Any())
        {
            _logger.LogDebug("backgroundReqChecker:   No pending documents found that need processing.");

            return new OutputData(); // Return empty output if no documents to process
        }

        OutputData output = new OutputData();
        List<RequestAPIDocument> outputMessages = new List<RequestAPIDocument>();
        List<RequestAPIDocument> DBOutputMessages = new List<RequestAPIDocument>();

        foreach (var document in pendingDocuments)
        {
            _logger.LogInformation("backgroundReqChecker:   Found pending document with ID: {id}, created at: {createdAt}, backgroundRequestId: {backgroundRequestId}",
                document.id, document.createdAt, document.backgroundRequestId);

            // Process the document here or send it to a queue for processing
            outputMessages.Add(document);
            var document2 = document.DeepCopy();
            document2.status = RequestAPIStatusEnum.ReSubmitted;
            DBOutputMessages.Add(document2);
        }

        output.QMessages = outputMessages.ToArray();
        output.DBMessages = DBOutputMessages.ToArray();

        _logger.LogInformation("backgroundReqChecker:   Retrieved {count} pending documents that need processing", outputMessages.Count);

        return output;
    }
}