using System.Collections.Concurrent;
using SimpleL7Proxy.Backend;
using Microsoft.Extensions.Options;

namespace SimpleL7Proxy.Events
{

  public class ProxyEvent : ConcurrentDictionary<string, string>
  {
    private static IOptions<BackendOptions> _options = null!;
    private static IEventClient? _eventHubClient;

    public static void Initialize(IOptions<BackendOptions> backendOptions, IEventClient? eventHubClient = null)
    {
      _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
      _eventHubClient = eventHubClient ?? throw new ArgumentNullException(nameof(eventHubClient));
    }

    public ProxyEvent() : base()
    {
      base["Ver"] = Constants.VERSION;
      base["Revision"] = _options.Value.Revision;
      base["ContainerApp"] = _options.Value.ContainerApp;
    }

    public ProxyEvent(ProxyEvent other) : base(other)
    {
      if (other == null) throw new ArgumentNullException(nameof(other));
    }

    public void SendEvent()
    {
      _eventHubClient?.SendData(this);
    }

    public void WriteOutput(string data = "")
    {

      try
      {
        // Log the data to the console
        if (!string.IsNullOrEmpty(data))
        {
          Console.WriteLine(data);
          this["Message"] = data;
        }

        if (!TryGetValue("Type", out var typeValue))
        {
          this["Type"] = "S7P-Console";
        }

        if (_options.Value.LogConsoleEvent)
        {
          _eventHubClient?.SendData(this);
        }
      }
      catch (Exception ex)
      {
        // Handle any exceptions that occur during logging
        Console.WriteLine($"Error writing output: {ex.Message}");
      }
    }
  }
}

