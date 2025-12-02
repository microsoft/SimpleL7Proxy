using Shared.RequestAPI.Models;
using SimpleL7Proxy;

namespace SimpleL7Proxy.BackupAPI
{

    public interface IBackupAPIService
    {
        Task StopAsync(CancellationToken cancellationToken);
        bool UpdateStatus(RequestAPIDocument message);
        
        /// <summary>
        /// Gets statistics for events sent in the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is event count.
        /// </summary>
        Dictionary<int, int> GetEventStatistics();
        
        /// <summary>
        /// Gets error statistics for the last 10 minutes.
        /// Returns a dictionary where key is minutes ago (0-9) and value is error count.
        /// </summary>
        Dictionary<int, int> GetErrorStatistics();
    }
}