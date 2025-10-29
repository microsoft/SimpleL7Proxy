using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Backend;

public interface IHostHealthCollection
{
  List<BaseHostHealth> Hosts { get; }
}
