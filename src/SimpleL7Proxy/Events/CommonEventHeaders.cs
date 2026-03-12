using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class CommonEventHeaders(IOptions<BackendOptions> options) : ICommonEventData
{
  private readonly FrozenDictionary<string, string> _defaultEventData =
    new Dictionary<string, string>
    {
      ["Ver"] = Constants.VERSION,
      ["Revision"] = options.Value.Revision,
      ["ContainerApp"] = options.Value.ContainerApp
    }.ToFrozenDictionary();

  public FrozenDictionary<string, string> DefaultEventData() => _defaultEventData;
}