using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Events
{
  public interface IEventClient
  {
    void SendData(string? value);
    void SendData(ProxyEvent eventData);
  }
}
