namespace SimpleL7Proxy.Events;

public class ProxyEvent
{
  public Dictionary<string, string> EventData { get; private set; } = [];
  public string Name { get; set; } = string.Empty;
}
