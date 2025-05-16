using System.Net;


namespace SimpleL7Proxy.Proxy
{
    /// <summary>
    /// Represents an asynchronous worker that performs a task and disappears after completion.
    /// </summary>
    public class AsyncHeaders
    {
        public required WebHeaderCollection Headers { get; set; }
        public required string UserId { get; set; }
        public required string MID { get; set; }
        public required string Guid { get; set; } 
        public required String Status { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public required string BlobUri { get; set; }
    }
}