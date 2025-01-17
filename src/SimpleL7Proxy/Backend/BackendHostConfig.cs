using Microsoft.Azure.Amqp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Backend
{
  public class BackendHostConfig
  {
    public readonly string Host;
    public readonly string ProbePath;
    public readonly string Protocol;
    public readonly int Port;

    public string Url => new UriBuilder(Protocol, Host, Port).Uri.AbsoluteUri;

    public string ProbeUrl => WebUtility.UrlDecode(Path.Combine(Url, ProbePath));


    public BackendHostConfig(string host, string? probepath)
    {
      Host = host;
      ProbePath = probepath?.TrimStart('/') ?? "echo/resource?param1=sample";

      // If host does not have a protocol, add one
      if (!Host.StartsWith("http://") && !Host.StartsWith("https://"))
      {
        Host = "https://" + Host;
      }

      // if host ends with a slash, remove it
      if (Host.EndsWith('/'))
      {
        Host = Host[..^1];
      }

      // parse the host, prototol and port
      Uri uri = new(Host);
      Protocol = uri.Scheme;
      Port = uri.Port;
      Host = uri.Host;
    }
  }
}
