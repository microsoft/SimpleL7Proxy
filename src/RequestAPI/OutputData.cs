using System;
using System.Collections.Generic;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Shared.RequestAPI.Models;

namespace RequestAPI;

public class OutputData
{
    [CosmosDBOutput(
        databaseName: "%CosmosDb:DatabaseName%",
        containerName: "%CosmosDb:ContainerName%",
        Connection = "CosmosDbConnection",
        CreateIfNotExists = true)]

    public RequestAPIDocument[] DBMessages { get; set; } = Array.Empty<RequestAPIDocument>();

    [ServiceBusOutput("%SBFeederQueue%", Connection = "ServiceBusConnection")]
    public RequestAPIDocument[] QMessages { get; set; } = Array.Empty<RequestAPIDocument>();

}