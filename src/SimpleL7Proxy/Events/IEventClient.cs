namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  public void StopTimer();

  void SendData(string? value);
  void SendData(ProxyEvent eventData);
}
