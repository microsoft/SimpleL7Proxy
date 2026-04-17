namespace SimpleL7Proxy.ServiceBus
{
    public enum ServiceBusMessageStatusEnum
    {
        None,               /// 0 - None
        Queued,             /// 1 - The message is in the queue.
        RetryScheduled,     /// 2 - The message has been dequeued for processing.     
        Requeued,           /// 3 - The message has been requeued.     
        Processing,         /// 4 - The message is being processed.
        CheckingBackgroundRequestStatus,    /// 5 - The message is having its background request status checked.    
        AsyncProcessingError,   /// 6 - The message could not be processed asynchronously due to an error.     
        AsyncProcessing,    /// 7 - The message is being processed asynchronously.  
        Processed,          /// 8 - The message has been successfully processed.
        AsyncProcessed,     /// 9 - The message has been processed asynchronously.      
        BackgroundRequestSubmitted,      /// 10 - The background request has been submitted.
        Failed,             /// 11 - The message processing has failed.
        Expired             /// 12 - The message has expired.
    }
}