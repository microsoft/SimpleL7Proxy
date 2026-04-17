namespace SimpleL7Proxy.Async.Feeder
{
    public interface IAsyncFeeder
    {
        Task StopAsync(CancellationToken cancellationToken);
    }
}