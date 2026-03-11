using System.Collections.Frozen;
using Microsoft.Extensions.Options;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Events;

public class CommonEventData : ICommonEventData
{
  private readonly FrozenDictionary<string, string> _defaultEventData;

  public FrozenDictionary<string, string> DefaultEventData() => _defaultEventData;

  public CommonEventData(IOptions<BackendOptions> options)
  {
    ArgumentNullException.ThrowIfNull(options);
    var bo = options.Value;

    _defaultEventData = new Dictionary<string, string>()
    {
      ["Ver"] = Constants.VERSION,
      ["Revision"] = bo.Revision,
      ["ContainerApp"] = bo.ContainerApp
    }.ToFrozenDictionary();
  }
}
