namespace Shared.RequestAPI.Models
{
    public enum RequestAPIStatusEnum
    {
        New,                  // 0
        InProgress,           // 1
        Completed,            // 2
        Failed,               // 3
        NeedsReprocessing,    // 4
        ReSubmitted,          // 5
        ReProcessing,         // 6
        BackgroundProcessing  // 7
    }
}
