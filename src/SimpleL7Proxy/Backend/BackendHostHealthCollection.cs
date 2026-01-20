using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Backend;

public class HostHealthCollection : IHostHealthCollection
{
  public List<BaseHostHealth> Hosts { get; private set; } = [];
  public List<BaseHostHealth> SpecificPathHosts { get; private set; } = [];
  public List<BaseHostHealth> CatchAllHosts { get; private set; } = [];

  public HostHealthCollection(IOptions<BackendOptions> options, ILogger<HostHealthCollection> logger)
  {
    if (options == null) throw new ArgumentNullException(nameof(options));
    if (options.Value == null) throw new ArgumentNullException(nameof(options.Value));
    if (options.Value.Hosts == null) throw new ArgumentNullException(nameof(options.Value.Hosts));

    foreach (var hostConfig in options.Value.Hosts)
    {
      BaseHostHealth host;

      // Determine if host supports probing based on DirectMode or ProbePath
      if (hostConfig.DirectMode || string.IsNullOrEmpty(hostConfig.ProbePath) || hostConfig.ProbePath == "/")
      {
        // No probe path or root path - treat as non-probeable
        host = new NonProbeableHostHealth(hostConfig, logger);
      }
      else
      {
        // Has a specific probe path - treat as probeable
        host = new ProbeableHostHealth(hostConfig, logger);
      }

      Hosts.Add(host);

      // Categorize by PartialPath
      var hostPartialPath = hostConfig.PartialPath?.Trim();

      if (string.IsNullOrEmpty(hostPartialPath) ||
          hostPartialPath == "/" ||
          hostPartialPath == "/*")
      {
        CatchAllHosts.Add(host);
      }
      else
      {
        SpecificPathHosts.Add(host);
      }
    }

    logger.LogInformation("Host categorization complete: {SpecificCount} specific hosts, {CatchAllCount} catch-all hosts",
        SpecificPathHosts.Count, CatchAllHosts.Count);
  }
}