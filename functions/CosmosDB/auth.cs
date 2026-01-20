using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
//using Newtonsoft.Json;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;


namespace Company.Function
{
    // public class auth
    // {
    //     private readonly ILogger<auth> _logger;

    //     public auth(ILogger<auth> logger)
    //     {
    //         _logger = logger;
    //     }

    //     [NoFunction("auth")]
    //     public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req,
    //                              [CosmosDBInput(
    //                              databaseName: "customer",
    //                              containerName: "profiles",
    //                              Connection  = "CosmosDBConnection",
    //                              SqlQuery = "select * from c")] IEnumerable<object> documents, 
    //                              FunctionContext context)
    //     {
    //         _logger.LogInformation("C# HTTP trigger function processed a request.");
    //         // return the Json data from CosmosDB

    //         //remove the following key values: id, _rid, _self, _etag, _attachments, _ts
    //         var response = documents.Select(document =>
    //         {
    //             var documentDict = JsonConvert.DeserializeObject<ExpandoObject>(document.ToString());
    //             var documentDictAsDict = (IDictionary<string, object>)documentDict;

    //             documentDictAsDict.Remove("id");
    //             documentDictAsDict.Remove("_rid");
    //             documentDictAsDict.Remove("_self");
    //             documentDictAsDict.Remove("_etag");
    //             documentDictAsDict.Remove("_attachments");
    //             documentDictAsDict.Remove("_ts");

    //             return documentDict;
    //         }).ToArray();

    //         return new JsonResult(response);
    //     }
    // }
}
