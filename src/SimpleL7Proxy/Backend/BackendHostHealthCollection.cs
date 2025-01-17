using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Backend;

public class BackendHostHealthCollection : IBackendHostHealthCollection
{
  public List<BackendHostHealth> Hosts { get; } = [];

  public BackendHostHealthCollection(IOptions<BackendOptions> options, ILogger<BackendHostHealth> logger)
  {
    var backendOptions = options.Value;

    foreach (var hostConfig in backendOptions.Hosts)
    {
      Hosts.Add(new BackendHostHealth(hostConfig, logger));
    }
  }
}
