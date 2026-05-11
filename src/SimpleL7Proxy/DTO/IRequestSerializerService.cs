namespace SimpleL7Proxy.DTO
{
    public interface IRequestSerializerService
    {
        Task BackupAsync(RequestData requestData);
        Task RestoreIntoAsync(RequestData data);

        Task<bool> DeleteBackupAsync(string blobname);
        // void ApplyToRequestData(RequestData requestData, RequestDataDtoV1 dto);
    }
}