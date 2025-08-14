namespace SimpleL7Proxy.Proxy
{
    /// <summary>
    /// Represents an asynchronous worker that performs a task and disappears after completion.
    /// </summary>
    public class AsyncMessage
    {
        public required string Message { get; set; }
        public required string UserId { get; set; }
        public required string MID { get; set; }
        public required string Guid { get; set; } 
        public required int Status { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public required string DataBlobUri { get; set; }
        public required string HeaderBlobUri { get; set; }
    }
}