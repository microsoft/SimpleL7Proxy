
using System.Text.Json;

namespace SimpleL7Proxy.DTO
{
    public class RequestDataDtoV1
    {
        public Guid Guid { get; set; }
        public string Method { get; set; }
        public string Path { get; set; }
        public string FullURL { get; set; }
        public byte[]? BodyBytes { get; set; }
        public DateTime Timestamp { get; set; }
        public DateTime EnqueueTime { get; set; }
        public DateTime DequeueTime { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string ExpiresAtString { get; set; }
        public string ExpireReason { get; set; }
        public int Priority { get; set; }
        public int Priority2 { get; set; }
        public int Timeout { get; set; }
        public int Attempts { get; set; }
        public string MID { get; set; }
        public string ParentId { get; set; }
        public string UserID { get; set; }
        public bool AsyncTriggered { get; set; }
        public bool Requeued { get; set; }
        public string SBTopicName { get; set; }
        public string BlobContainerName { get; set; }
        public int AsyncBlobAccessTimeoutSecs { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public int version { get; set; } = 1;

        public RequestDataDtoV1(RequestData data)
        {
            Guid = data.Guid;
            Method = data.Method;
            Path = data.Path;
            FullURL = data.FullURL;
            BodyBytes = data.BodyBytes;
            Timestamp = data.Timestamp;
            EnqueueTime = data.EnqueueTime;
            DequeueTime = data.DequeueTime;
            ExpiresAt = data.ExpiresAt;
            ExpiresAtString = data.ExpiresAtString;
            ExpireReason = data.ExpireReason;
            Priority = data.Priority;
            Priority2 = data.Priority2;
            Timeout = data.Timeout;
            Attempts = data.Attempts;
            MID = data.MID;
            ParentId = data.ParentId;
            UserID = data.UserID;
            AsyncTriggered = data.AsyncTriggered;
            Requeued = data.Requeued;
            SBTopicName = data.SBTopicName;
            BlobContainerName = data.BlobContainerName;
            AsyncBlobAccessTimeoutSecs = data.AsyncBlobAccessTimeoutSecs;

            // Convert WebHeaderCollection to Dictionary
            if (data.Headers != null)
            {
                Headers = data.Headers.AllKeys
                    .Where(k => k != null)
                    .ToDictionary(k => k!, k => data.Headers[k] ?? "");
            }
            else
            {
                Headers = new Dictionary<string, string>();
            }
        }

        public RequestDataDtoV1()
        {
            // Parameterless constructor for deserialization
            Method = string.Empty;
            Path = string.Empty;
            FullURL = string.Empty;
            ExpiresAtString = string.Empty;
            ExpireReason = string.Empty;
            MID = string.Empty;
            ParentId = string.Empty;
            UserID = string.Empty;
            SBTopicName = string.Empty;
            BlobContainerName = string.Empty;
            Headers = new Dictionary<string, string>();
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }

        public static RequestDataDtoV1? Deserialize(string json)
        {
            // First, deserialize just to check version
            var versionCheck = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

            if (versionCheck != null && versionCheck.TryGetValue("version", out var versionObj))
            {
                var version = Convert.ToInt32(versionObj);
                
                switch (version)
                {
                    case 1:
                        return JsonSerializer.Deserialize<RequestDataDtoV1>(json);
                    // Add more versions here as needed
                    // case 2:
                    //     var v2 = JsonConvert.DeserializeObject<RequestDataDtoV2>(json);
                    //     return ConvertV2ToV1(v2);
                    default:
                        throw new NotSupportedException($"RequestData version {version} is not supported");
                }
            }

            // If no version, assume V1
            return JsonSerializer.Deserialize<RequestDataDtoV1>(json);
        }

    }
}