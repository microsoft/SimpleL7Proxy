namespace SimpleL7Proxy.BlobStorage
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading.Tasks;
    using Azure.Storage.Blobs;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Exception thrown when there is an error writing to Azure Blob Storage.
    /// </summary>
    public class BlobWriterException : Exception
    {

        public string BlobName { get; set; } = "N/A";
        public string ContainerName { get; set; } = "N/A";
        public string Guid { get; set; } = "N/A";
        public string MID { get; set; } = "N/A";
        public string Operation { get; set; } = "N/A";
        public string UserId { get; set; } = "N/A";
        public BlobWriterException(string message) : base(message) { }
        public BlobWriterException(string message, Exception innerException) : base(message, innerException) { }

    }
}