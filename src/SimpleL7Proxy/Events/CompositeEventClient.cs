using System.Collections.Frozen;

namespace SimpleL7Proxy.Events;

/// <summary>
/// Thread-safe composite that fans out SendData to every registered IEventClient.
/// Clients add themselves via <see cref="Add"/> once they have initialised successfully.
/// Clients are never removed — logging is critical and no events may be lost.
/// Each client's own drain / shutdown logic handles graceful teardown.
/// 
/// The hot-path (<see cref="SendData"/>) reads from a <see cref="FrozenDictionary{TKey,TValue}"/>
/// snapshot that is rebuilt on every Add, giving zero-overhead iteration with no locking.
/// </summary>
public class CompositeEventClient : IEventClient
{
  private readonly object _lock = new();
  private readonly Dictionary<IEventClient, byte> _mutable = new();
  private volatile FrozenDictionary<IEventClient, byte> _frozen = FrozenDictionary<IEventClient, byte>.Empty;

  /// <summary>
  /// Register a client. Safe to call from any thread (e.g. inside StartAsync).
  /// Re-freezes the snapshot so subsequent SendData calls include the new client.
  /// </summary>
  public void Add(IEventClient client)
  {
    ArgumentNullException.ThrowIfNull(client);
    lock (_lock)
    {
      _mutable[client] = 0;
      _frozen = _mutable.ToFrozenDictionary();
    }
    Console.WriteLine($"[CompositeEventClient] Added {client.ClientType}");
  }

  public async Task StopTimerAsync()
  {
    List<Task> stopTasks = new();

    foreach (var client in _frozen.Keys)
    {
      Console.WriteLine($"Stopping timer for {client.ClientType}");
      stopTasks.Add(client.StopTimerAsync());
    }
    await Task.WhenAll(stopTasks).ConfigureAwait(false);
  }

  // Return the max count of all the clients
  public int Count
  {
    get
    {
      var snapshot = _frozen;
      return snapshot.Count == 0 ? 0 : snapshot.Keys.Max(c => c.Count);
    }
  }

  public bool IsHealthy()
  {
    foreach (var client in _frozen.Keys)
    {
      if (!client.IsHealthy())
      {
        return false;
      }
    }
    return true;
  }

  public void BeginShutdown()
  {
    foreach (var client in _frozen.Keys)
    {
      client.BeginShutdown();
    }
  }


  public string ClientType
  {
    get
    {
      var snapshot = _frozen;
      return snapshot.Count == 0
        ? "Composite (empty)"
        : string.Join(", ", snapshot.Keys.Select(c => c.ClientType));
    }
  }

  public void SendData(string? value)
  {
    foreach (var client in _frozen.Keys)
    {
      client.SendData(value);
    }
  }
}
