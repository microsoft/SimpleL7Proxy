namespace Shared.RequestAPI.Models
{
    public enum RequestAPIStatusEnum
    {
        New,                  // 0
        InProgress,           // 1  Proxy is processing it
        Completed,            // 2  Proxy has completed processing
        Failed,               // 3  Proxy has failed to process
        NeedsReprocessing,    // 4  Waiting for the API to re-submit the request
        ReSubmitted,          // 5  API has re-submitted the request
        ReProcessing,         // 6  Proxy will re-process the request
        BackgroundProcessing,  // 7 Proxy is processing it in background mode
    }
}
