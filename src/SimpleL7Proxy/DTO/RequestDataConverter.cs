using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.RequestAPI.Models;

namespace SimpleL7Proxy.DTO
{
    public static class RequestDataConverter
    {
        public static RequestAPIDocument ToRequestAPIDocument(this RequestData data)
        {
            return new RequestAPIDocument
            {
                backgroundRequestId = data.BackgroundRequestId,
                createdAt = data.EnqueueTime,
                guid = data.Guid.ToString(),
                id = data.Guid.ToString(),
                isAsync = true,
                isBackground = data.IsBackground,
                mid = data.MID,
                priority1 = data.Priority,
                priority2 = data.Priority2,
                status = RequestAPIStatusEnum.New,    // careful if copying from request data and it already has a status
                userID = data.profileUserId,
                URL = data.FullURL
            };
        }

        public static RequestDataDtoV1? Deserialize(string json)
        {
            return RequestDataDtoV1.Deserialize(json);
        }

        public static RequestDataDtoV1? DeserializeWithVersionHandling(string json)
        {
            var operation = "Checking version";
            try
            {
                // First, deserialize to JsonDocument to safely handle version check
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                try
                {
                    if (root.TryGetProperty("version", out var versionElement))
                    {
                        operation = "Getting version";
                        var version = versionElement.GetInt32(); // Safely get integer value

                        switch (version)
                        {
                            case 1:
                                operation = "Deserializing V1";
                                return RequestDataDtoV1.Deserialize(json);
                            // Add more versions here as needed
                            // case 2:
                            //     var v2 = JsonConvert.DeserializeObject<RequestDataDtoV2>(json);
                            //     return ConvertV2ToV1(v2);
                            default:
                                throw new NotSupportedException($"RequestData version {version} is not supported");
                        }
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during version check: {ex.Message}");
                    throw;
                }
                operation = "Deserializing as V1 - default";
                // If no version, assume V1
                return RequestDataDtoV1.Deserialize(json);
            }
            catch (JsonException ex)
            {
                // Handle JSON deserialization errors
                Console.WriteLine($"Error deserializing.  operation: {operation},  RequestData: {ex.Message}");
                return null;
            }
        }
    }
}