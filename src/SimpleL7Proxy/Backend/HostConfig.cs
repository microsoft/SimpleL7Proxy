using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Backend
{

  /// <summary>
  /// Handles configuration and token management for a single backend host.
  /// </summary>
  public class HostConfig
  {
    public static BackendTokenProvider? _tokenProvider;
    private static ILogger? _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public Guid Guid { get; } = Guid.NewGuid();
    public string Host { get; private set; }
    public string ProbePath { get; private set; }
    public string Protocol { get; private set; }
    public int Port { get; private set; }
    public string? IpAddr { get; private set; }
    public bool DirectMode { get; private set; }
    public string PartialPath { get; private set; } = "/";
    public bool UseOAuth { get; private set; }
    public string Audience { get; private set; } = "";

    private struct ParsedConfig
    {
      public string Host;
      public string ProbePath;
      public bool DirectMode;
      public string? IpAddr;
      public string PartialPath;
      public bool UseOAuth;
      public string Audience;
    }


    public string Url => new UriBuilder(Protocol, IpAddr ?? Host, Port).Uri.AbsoluteUri;
    /// <summary>
    /// Full URL for backend host.
    /// </summary>
    public string ProbeUrl => WebUtility.UrlDecode(new UriBuilder(Protocol, IpAddr ?? Host, Port, ProbePath).Uri.AbsoluteUri);
    /// <summary>
    /// Decoded probe URL for health checks.
    /// </summary>

    public static void Initialize(BackendTokenProvider tokenProvider, ILogger logger)
    {
      _tokenProvider = tokenProvider;
      _logger = logger;
    }

    // Can pass in hostname  and probepath
    // or
    // hostname=host=<host:port>;probe=<path>;mode=<direct|apim|>;ipaddress=<ipaddress>;path=<partialpath>
    /// <summary>
    /// Constructs a BackendHostConfig from a hostname and optional probe path.
    /// </summary>
    public HostConfig(string hostname, string? probepath = "", string? audience = "")
    {
      _logger?.LogInformation("[CONFIG] Configuring backend host: {hostname}", hostname);
      var parsed = TryParseConfig(hostname, probepath, audience);

      // If host does not have a protocol, add one
      string hostForUri = parsed.Host;
      if (!hostForUri.StartsWith("http://") && !hostForUri.StartsWith("https://"))
      {
        hostForUri = "https://" + hostForUri;
      }

      // if host ends with a slash, remove it
      hostForUri = hostForUri.TrimEnd('/');

      // parse the host, protocol and port
      Uri uri = new Uri(hostForUri);
      Protocol = uri.Scheme;
      Port = uri.Port;
      Host = uri.Host;
      ProbePath = parsed.ProbePath;
      DirectMode = parsed.DirectMode;
      IpAddr = parsed.IpAddr;
      PartialPath = parsed.PartialPath;
      UseOAuth = parsed.UseOAuth;
      Audience = parsed.Audience;

      if (DirectMode)
      {
        _logger?.LogInformation("[CONFIG] ✓ Direct host configured: {Host} | Probe: {ProbePath} Path: {PartialPath}", Host, ProbePath, PartialPath);
      }
      else
      {
        _logger?.LogInformation("[CONFIG] ✓ APIM host configured: {Host} | Probe: {ProbePath}", Host, ProbePath);
      }
    }

    private static ParsedConfig TryParseConfig(string input, string? probepath, string? audience = "")
    /// <summary>
    /// Parses a backend configuration string into a ParsedConfig struct.
    /// </summary>
    {
      var result = new ParsedConfig
      {
        Host = input,
        ProbePath = probepath?.TrimStart('/') ?? "echo/resource?param1=sample",
        DirectMode = false,
        IpAddr = null,
        PartialPath = "/",
        UseOAuth = false,
        Audience = audience ?? ""
      };

      if (input.Contains(';'))
      {
        var parts = input.Split(';');
        var configDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in parts)
        {
          var trimmed = part.Trim();
          var idx = trimmed.IndexOf('=');
          if (idx <= 0 || idx == trimmed.Length - 1)
            throw new UriFormatException($"Invalid backend host configuration part: {part}");

          var key = trimmed.Substring(0, idx).Trim();
          var value = trimmed.Substring(idx + 1).Trim();
          configDict[key] = value;
        }

        foreach (var kvp in configDict)
        {
          switch (kvp.Key.ToLowerInvariant())
          {
            case "probe":
              result.ProbePath = kvp.Value;
              break;
            case "mode":
              result.DirectMode = kvp.Value.Equals("direct", StringComparison.OrdinalIgnoreCase);
              break;
            case "ipaddress":
              result.IpAddr = kvp.Value;
              break;
            case "host":
              result.Host = kvp.Value;
              break;
            case "path":
              result.PartialPath = kvp.Value;
              break;
            case "useoauth":
              result.UseOAuth = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
              break;
            case "audience":
              result.Audience = kvp.Value;
              break;
            default:
              throw new UriFormatException($"Invalid backend host configuration key: {kvp.Key}");
          }
        }
      }
      return result;
    }

    public void RegisterWithTokenProvider()
    {
        if (UseOAuth && !string.IsNullOrEmpty(Audience))
        {
            _tokenProvider!.AddAudience(Audience);
        }
    }

    /// <summary>
    /// Gets the current OAuth2 token for this backend host.
    /// </summary>
    public async Task<string> OAuth2Token()
    {
      if (!UseOAuth || string.IsNullOrEmpty(Audience) || _tokenProvider == null)
        return string.Empty;

      return await _tokenProvider.OAuth2Token(Audience).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the destination URL for a request to this backend host.
    /// </summary>
    public string BuildDestinationUrl(string requestPath)
    {
      var urlWithPath = new UriBuilder(Url) { Path = requestPath }.Uri.AbsoluteUri;
      return WebUtility.UrlDecode(urlWithPath);
    }

  }
}
