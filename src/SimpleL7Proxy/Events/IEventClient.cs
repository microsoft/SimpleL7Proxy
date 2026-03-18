using System.Collections.Concurrent;

namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  int Count { get; }
  string ClientType { get; }
  bool IsHealthy();
  public Task StopTimerAsync();
  void SendData(string? value);

}
