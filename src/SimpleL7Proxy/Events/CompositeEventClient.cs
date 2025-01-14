namespace SimpleL7Proxy.Events;

public class CompositeEventClient(IEnumerable<IEventClient> eventClients)
  : IEventClient
{
  public void SendData(string? value)
  {
    foreach (var client in eventClients)
    {
      client.SendData(value);
    }
  }

  public void SendData(ProxyEvent proxyEvent)
  {
    foreach (var client in eventClients)
    {
      client.SendData(proxyEvent);
    }
  }
}
