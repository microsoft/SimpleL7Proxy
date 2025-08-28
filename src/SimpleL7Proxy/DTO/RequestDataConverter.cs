using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleL7Proxy.DTO
{
    public static class RequestDataConverter
    {

        public static RequestDataDtoV1? Deserialize(string json)
        {
            return RequestDataDtoV1.Deserialize(json);
        }

        public static RequestDataDtoV1? DeserializeWithVersionHandling(string json)
        {
            // First, deserialize just to check version
            var versionCheck = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (versionCheck != null && versionCheck.TryGetValue("version", out var versionObj))
            {
                var version = Convert.ToInt32(versionObj);
                
                switch (version)
                {
                    case 1:
                        return RequestDataDtoV1.Deserialize(json);
                    // Add more versions here as needed
                    // case 2:
                    //     var v2 = JsonConvert.DeserializeObject<RequestDataDtoV2>(json);
                    //     return ConvertV2ToV1(v2);
                    default:
                        throw new NotSupportedException($"RequestData version {version} is not supported");
                }
            }

            // If no version, assume V1
            return RequestDataDtoV1.Deserialize(json);
        }
    }
}