namespace SimpleL7Proxy.Events;

public class CompositeEventClient : IEventClient
{
  private readonly IEnumerable<IEventClient> _eventClients;

  public CompositeEventClient(IEnumerable<IEventClient> eventClients)
  {
    _eventClients = eventClients;
  }

  public void SendData(string? value)
  {
    foreach (var client in _eventClients)
    {
      client.SendData(value);
    }
  }

  public void SendData(ProxyEvent proxyEvent)
  {
    foreach (var client in _eventClients)
    {
      client.SendData(proxyEvent);
    }
  }
}
