using System.Collections.Frozen;
namespace SimpleL7Proxy.Events;

public interface ICommonEventData
{
  FrozenDictionary<string, string> DefaultEventData();
}
