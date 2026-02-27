using System.Collections.Concurrent;

namespace SimpleL7Proxy.Events;

public class CompositeEventClient(IEnumerable<IEventClient> eventClients)
  : IEventClient
{
  public async Task StopTimerAsync()
  {
    foreach (var client in eventClients)
    {
      Console.WriteLine($"Stopping timer for {client}");
      await client.StopTimerAsync().ConfigureAwait(false);
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
  public string ClientType => string.Join(", ", eventClients.Select(c => c.ClientType));
  public void SendData(string? value)
  {
    foreach (var client in eventClients)
    {
      client.SendData(value);
    }
  }
}
