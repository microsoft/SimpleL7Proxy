using System.Collections.Concurrent;

namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  int Count { get; }
  //public Task StartTimer();
  public void StopTimer();

  void SendData(string? value);
 void SendData(Dictionary<string, string> data);
  void SendData( ConcurrentDictionary<string, string> eventData, string? name="");
  void SendData(ProxyEvent eventData);
}
