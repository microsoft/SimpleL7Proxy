namespace SimpleL7Proxy.DTO
{
    public interface IRequestDataBackupService
    {
        Task BackupAsync(RequestData requestData);
        Task<RequestData> RestoreAsync(string blobname);

        Task<bool> DeleteBackupAsync(string blobname);
        // void ApplyToRequestData(RequestData requestData, RequestDataDtoV1 dto);
    }
}