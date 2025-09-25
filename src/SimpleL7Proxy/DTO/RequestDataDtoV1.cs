
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleL7Proxy.DTO
{
    public class RequestDataDtoV1
    {
        public bool Requeued { get; set; }
        public DateTime DequeueTime { get; set; }
        public DateTime EnqueueTime { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public Guid Guid { get; set; }
        public List<Dictionary<string, string>> IncompleteRequests { get; set; }
        public int AsyncBlobAccessTimeoutSecs { get; set; }
        public int Attempts { get; set; }
        public int Priority { get; set; }
        public int Priority2 { get; set; }
        public int Timeout { get; set; }
        public int version { get; set; } = 1;
        public ProxyEventDto ProxyEvent { get; set; }
        public string BlobContainerName { get; set; }
        public string FullURL { get; set; }
        public string Method { get; set; }
        public string MID { get; set; }
        public string ParentId { get; set; }
        public string Path { get; set; }
        public string profileUserId { get; set; }
        public string SBTopicName { get; set; }
        public string UserID { get; set; }

        public RequestDataDtoV1(RequestData data)
        {
            AsyncBlobAccessTimeoutSecs = data.AsyncBlobAccessTimeoutSecs;
            Attempts = data.Attempts;
            BlobContainerName = data.BlobContainerName;
            DequeueTime = data.DequeueTime;
            EnqueueTime = data.EnqueueTime;
            ExpiresAt = data.ExpiresAt;
            FullURL = data.FullURL;
            Guid = data.Guid;
            IncompleteRequests = data.incompleteRequests;
            Method = data.Method;
            MID = data.MID;
            ParentId = data.ParentId;
            Path = data.Path;
            Priority = data.Priority;
            Priority2 = data.Priority2;
            profileUserId = data.profileUserId;
            Requeued = data.Requeued;
            SBTopicName = data.SBTopicName;
            Timeout = data.Timeout;
            Timestamp = data.Timestamp;
            UserID = data.UserID;

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

            if (data.EventData != null)
            {
                ProxyEvent = ProxyEventDto.FromProxyEvent(data.EventData);
            }
            else
            {
                ProxyEvent = new ProxyEventDto();
            }
        }

        public RequestDataDtoV1()
        {
            // Parameterless constructor for deserialization
            BlobContainerName = string.Empty;
            FullURL = string.Empty;
            Headers = new Dictionary<string, string>();
            IncompleteRequests = new List<Dictionary<string, string>>();
            Method = string.Empty;
            MID = string.Empty;
            ParentId = string.Empty;
            Path = string.Empty;
            profileUserId = string.Empty;
            ProxyEvent = new ProxyEventDto();
            SBTopicName = string.Empty;
            UserID = string.Empty;
        }

        public string Serialize()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        }

        public static RequestDataDtoV1? Deserialize(string json)
        {
            return JsonSerializer.Deserialize<RequestDataDtoV1>(json);
        }

        // public RequestData toRequestData()
        // {
        //     var requestData = new RequestData(Guid.ToString(), Guid, MID, Path, Method, Timestamp, Headers)
        //     {
        //         AsyncBlobAccessTimeoutSecs = this.AsyncBlobAccessTimeoutSecs,
        //         Attempts = Attempts,
        //         BlobContainerName = BlobContainerName,
        //         DequeueTime = DequeueTime,
        //         EnqueueTime = EnqueueTime,
        //         ExpiresAt = ExpiresAt,
        //         FullURL = FullURL,
        //         incompleteRequests = IncompleteRequests,
        //         ParentId = ParentId,
        //         Priority = Priority,
        //         Priority2 = Priority2,
        //         profileUserId = this.profileUserId,
        //         Requeued = Requeued,
        //         SBTopicName = SBTopicName,
        //         Timeout = Timeout,
        //         UserID = UserID
        //     };

        //     requestData.EventData = this.ProxyEvent.ToProxyEvent();

        //     return requestData;
        // }

        public void PopulateInto(RequestData data)
        {

            data.Populate(Guid.ToString(), Guid, MID, Path, Method, Timestamp, Headers);
            data.AsyncBlobAccessTimeoutSecs = this.AsyncBlobAccessTimeoutSecs;
            data.Attempts = Attempts;
            data.BlobContainerName = BlobContainerName;
            data.DequeueTime = DequeueTime;
            data.EnqueueTime = EnqueueTime;
            data.ExpiresAt = ExpiresAt;
            data.FullURL = FullURL;
            data.incompleteRequests = IncompleteRequests;
            data.ParentId = ParentId;
            data.Priority = Priority;
            data.Priority2 = Priority2;
            data.profileUserId = this.profileUserId;
            data.Requeued = Requeued;
            data.SBTopicName = SBTopicName;
            data.Timeout = Timeout;
            data.UserID = UserID;
            data.EventData = this.ProxyEvent.ToProxyEvent();

            return;
        }

    }
}