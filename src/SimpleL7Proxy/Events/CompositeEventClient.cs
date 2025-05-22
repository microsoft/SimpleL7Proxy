namespace SimpleL7Proxy.Events;

public class CompositeEventClient(IEnumerable<IEventClient> eventClients)
  : IEventClient
{


  public Task StartTimer()
  {
    foreach (var client in eventClients)
    {
      Console.WriteLine($"starting timer for {client}");
      client.StopTimer();
    }

    return Task.CompletedTask;
  }
  public void StopTimer()
  {
    foreach (var client in eventClients)
    {
      Console.WriteLine($"Stopping timer for {client}");
      client.StopTimer();
    }
  }
  public int Count
  {
    get
    {
      var count = 0;
      foreach (var client in eventClients)
      {
        count += client.Count;
      }
      return count;
    }
  }
  public void SendData(string? value)
  {
    foreach (var client in eventClients)
    {
      client.SendData(value);
    }
  }

  public void SendData(Dictionary<string, string> data)
  {
    foreach (var client in eventClients)
    {
      client.SendData(data);
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
