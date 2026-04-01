using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class CommonEventHeaders(IOptions<ProxyConfig> options) : ICommonEventData
{
  private readonly FrozenDictionary<string, string> _defaultEventData =
    new Dictionary<string, string>
    {
      ["Ver"] = Constants.VERSION,
      ["Replica"] = options.Value.ReplicaName,
      ["ContainerApp"] = options.Value.ContainerApp
    }.ToFrozenDictionary();

  public FrozenDictionary<string, string> DefaultEventData() => _defaultEventData;
}