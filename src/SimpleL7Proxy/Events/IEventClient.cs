using System.Collections.Concurrent;

namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  int Count { get; }
  string ClientType { get; }
  public Task StopTimerAsync();

  void SendData(string? value);
//  void SendData(Dictionary<string, string> data);
  //void SendData( ConcurrentDictionary<string, string> eventData, string? name="");
  void SendData(ProxyEvent eventData);
}
