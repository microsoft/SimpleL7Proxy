using System.Collections.Concurrent;
using Microsoft.Extensions.Options;


  public class ProxyEvent : ConcurrentDictionary<string, string>
  {
    private static IOptions<BackendOptions> _options = null!;

    public static void Initialize(IOptions<BackendOptions> backendOptions)
    {
      _options = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
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
  }

