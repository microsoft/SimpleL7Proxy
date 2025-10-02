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
    public Guid guid = Guid.NewGuid();
    public readonly string Host;
    public readonly string ProbePath;
    public readonly string Protocol;
    public readonly int Port;
    public readonly string? IpAddr;
  //  public string Url => new UriBuilder(Protocol, Host, Port).Uri.AbsoluteUri;
    public string Url => new UriBuilder(Protocol, IpAddr ?? Host, Port).Uri.AbsoluteUri;
    public string ProbeUrl => WebUtility.UrlDecode(new UriBuilder(Protocol, IpAddr ?? Host, Port, ProbePath).Uri.AbsoluteUri);


    public BackendHostConfig(string hostname, string? probepath)
    {
      ProbePath = probepath?.TrimStart('/') ?? "echo/resource?param1=sample";
      
      // If host does not have a protocol, add one
      if (!hostname.StartsWith("http://") && !hostname.StartsWith("https://"))
      {
        hostname = "https://" + hostname;
      }

      // if host ends with a slash, remove it
      if (hostname.EndsWith("/"))
      {
        hostname = hostname.Substring(0, hostname.Length - 1);
      }

      // parse the host, prototol and port
      Uri uri = new Uri(hostname);
      Protocol = uri.Scheme;
      Port = uri.Port;
      Host = uri.Host;

      // ProbePath = probepath ?? "echo/resource?param1=sample";
      // if (ProbePath.StartsWith("/"))
      // {
      //     ProbePath = ProbePath.Substring(1);
      // }

      // Uncomment UNTIL sslStream is implemented
      // if (ipaddress != null)
      // {
      //     // Valudate that the address is in the right format
      //     if (!System.Net.IPAddress.TryParse(ipaddress, out _))
      //     {
      //         throw new System.UriFormatException($"Invalid IP address: {ipaddress}");
      //     }
      //     ipaddr = ipaddress;
      // }


      Console.WriteLine($"[CONFIG] ✓ Backend host configured: {this.Host} | Probe: {this.ProbePath}");
    }
  }
}
