public interface IServer
{
    Task Run();
    void Start(CancellationToken cancellationToken);
}