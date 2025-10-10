using Shared.RequestAPI.Models;
using SimpleL7Proxy;

namespace SimpleL7Proxy.BackupAPI
{

    public interface IBackupAPIService
    {
        Task StopAsync(CancellationToken cancellationToken);
        bool UpdateStatus(RequestAPIDocument message);
    }
}