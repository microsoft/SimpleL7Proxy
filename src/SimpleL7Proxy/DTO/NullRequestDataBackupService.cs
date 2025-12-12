namespace SimpleL7Proxy.DTO;

public class NullRequestDataBackupService : IRequestDataBackupService
{
    public Task BackupAsync(RequestData requestData)
    {
        // NOP
        return Task.CompletedTask;
    }

    public Task RestoreIntoAsync(RequestData data)
    {
        // NOP
        return Task.CompletedTask;
    }

    public Task<bool> DeleteBackupAsync(string blobname)
    {
        // NOP
        return Task.FromResult(true);
    }
}
