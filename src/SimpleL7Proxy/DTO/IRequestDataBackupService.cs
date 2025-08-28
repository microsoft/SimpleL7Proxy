namespace SimpleL7Proxy.DTO
{
    public interface IRequestDataBackupService
    {
        Task BackupAsync(RequestData requestData);
        Task<RequestDataDtoV1?> RestoreAsync(string blobname);
        // void ApplyToRequestData(RequestData requestData, RequestDataDtoV1 dto);
    }
}