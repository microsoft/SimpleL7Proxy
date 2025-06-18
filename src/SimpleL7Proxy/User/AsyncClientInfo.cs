namespace SimpleL7Proxy.User
{
    /// <summary>
    /// Represents information about an asynchronous client.
    /// </summary>
    public class AsyncClientInfo
    {
        public string UserId { get; set; }
        public string ContainerName { get; set; }
        public string SBTopicName { get; set; }
        public int AsyncBlobAccessTimeoutSecs { get; set; } 

        public AsyncClientInfo(string userId, string containerName, string sbTopicName, int asyncBlobAccessTimeoutSecs = 3600)
        {
            UserId = userId;
            ContainerName = containerName;
            SBTopicName = sbTopicName;
            AsyncBlobAccessTimeoutSecs = asyncBlobAccessTimeoutSecs;
        }
    }
}