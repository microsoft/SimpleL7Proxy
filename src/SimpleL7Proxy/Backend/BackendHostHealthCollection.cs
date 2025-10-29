using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Backend;

public class HostHealthCollection : IHostHealthCollection
{
  public List<BaseHostHealth> Hosts { get; private set; } = [];

  public HostHealthCollection(IOptions<BackendOptions> options, ILogger<HostHealthCollection> logger)
  {
    if (options == null) throw new ArgumentNullException(nameof(options));
    if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
    if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));

    foreach (var hostConfig in options.Value.Hosts)
    {
      // Determine if host supports probing based on ProbePath
      if (string.IsNullOrEmpty(hostConfig.ProbePath) || hostConfig.ProbePath == "/")
      {
        // No probe path or root path - treat as non-probeable
        Hosts.Add(new NonProbeableHostHealth(hostConfig, logger));
      }
      else
      {
        // Has a specific probe path - treat as probeable
        Hosts.Add(new ProbeableHostHealth(hostConfig, logger));
      }
    }
  }
}