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
        public bool GenerateSasTokens { get; set; }

        public AsyncClientInfo(string userId, string containerName, string sbTopicName, int asyncBlobAccessTimeoutSecs = 3600, bool generateSasTokens = false)
        {
            UserId = userId;
            ContainerName = containerName;
            SBTopicName = sbTopicName;
            AsyncAllowed = asyncAllowed;
            AsyncBlobAccessTimeoutSecs = asyncBlobAccessTimeoutSecs;
            GenerateSasTokens = generateSasTokens;
        }
    }
}