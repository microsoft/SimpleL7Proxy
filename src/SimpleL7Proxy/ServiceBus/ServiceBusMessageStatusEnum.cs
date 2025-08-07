namespace SimpleL7Proxy.ServiceBus
{
    public enum ServiceBusMessageStatusEnum
    {
        None,
        /// <summary>
        /// The message is in the queue.
        /// </summary>
        InQueue,

        /// <summary>
        /// The message has been requeued.
        /// </summary>
        RetryAfterDelay,

        /// <summary>
        /// The message has been requeued.
        /// </summary>
        ReQueued,

        /// <summary>
        /// The message is being processed.
        /// </summary>
        Processing,

        /// <summary>
        /// The message is being processed asynchronously.
        /// </summary>
        AsyncProcessing,
        /// <summary>
        /// The message has been successfully processed.
        /// </summary>
        Processed,

        /// <summary>
        /// The message has been processed asynchronously.
        /// 
        AsyncProcessed,

        /// <summary>
        /// The message has failed to process.
        /// </summary>
        Failed,

        /// <summary>
        /// The message has expired.
        /// </summary>
        Expired

    }
}