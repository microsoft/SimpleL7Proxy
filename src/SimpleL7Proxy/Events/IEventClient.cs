using System.Collections.Concurrent;

namespace SimpleL7Proxy.Events;

public interface IEventClient
{
  int Count { get; }
  string ClientType { get; }
  bool IsHealthy();
  void BeginShutdown();         // begin agressively flushing!
  public Task StopTimerAsync(); // terminate!
  void SendData(string? value);

}
