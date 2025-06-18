using System.Collections.Concurrent;
using SimpleL7Proxy.Backend;
using Microsoft.Extensions.Options;

namespace SimpleL7Proxy.Events
{

  public class ProxyEvent : ConcurrentDictionary<string, string>
  {
    private static IOptions<BackendOptions> _options = null!;

    public static void Initialize(IOptions<BackendOptions> backendOptions)
    {
      _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
    }

    public ProxyEvent() : base()
    {
      try {
      base["Ver"] = Constants.VERSION;
      base["Revision"] = _options.Value.Revision;
      base["ContainerApp"] = _options.Value.ContainerApp;
    } catch (Exception ex)
      {
Console.WriteLine($"Error initializing ProxyEvent: {ex.Message}");
      }
    }
    
    public ProxyEvent(ProxyEvent other) : base(other)
    {
      if (other == null) throw new ArgumentNullException(nameof(other));
    }
  }

}