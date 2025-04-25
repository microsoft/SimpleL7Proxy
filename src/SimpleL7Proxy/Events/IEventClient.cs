namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  int Count { get; }
  public void StopTimer();

  void SendData(string? value);
  void SendData(ProxyEvent eventData);
}
