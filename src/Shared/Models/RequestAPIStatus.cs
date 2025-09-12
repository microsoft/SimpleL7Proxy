namespace Shared.RequestAPI.Models
{
    public enum RequestAPIStatusEnum
    {
        New,
        InProgress,
        Completed,
        Failed,
        NeedsReprocessing,
        ReSubmitted,
        ReProcessing
    }
}
