namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  void SendData(string? value);
  void SendData(ProxyEvent eventData);
}
