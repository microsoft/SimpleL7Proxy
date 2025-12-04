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
        public bool AsyncAllowed { get; set; } = false;
        public int AsyncBlobAccessTimeoutSecs { get; set; } 

        public AsyncClientInfo(string userId, string containerName, string sbTopicName, bool asyncAllowed, int asyncBlobAccessTimeoutSecs = 3600)
        {
            UserId = userId;
            ContainerName = containerName;
            SBTopicName = sbTopicName;
            AsyncAllowed = asyncAllowed;
            AsyncBlobAccessTimeoutSecs = asyncBlobAccessTimeoutSecs;
        }
    }
}