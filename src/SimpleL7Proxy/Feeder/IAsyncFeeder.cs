namespace SimpleL7Proxy.Feeder
{
    public interface IAsyncFeeder
    {
        Task StopAsync(CancellationToken cancellationToken);
    }
}