namespace SimpleL7Proxy.Async.Jobs
{
    public interface IAsyncFeeder
    {
        Task StopAsync(CancellationToken cancellationToken);
    }
}