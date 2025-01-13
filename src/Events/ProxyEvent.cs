using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Events;

public class ProxyEvent
{
  public Dictionary<string, string> EventData { get; private set; } = new Dictionary<string, string>();
  public string Name { get; set; } = string.Empty;
}
