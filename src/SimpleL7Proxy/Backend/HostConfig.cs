using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

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
    private static IServiceProvider? _serviceProvider;
    private readonly ICircuitBreaker _circuitBreaker;
    public Guid Guid { get; } = Guid.NewGuid();
    public string Host { get; private set; }
    public string ProbePath { get; private set; }
    public string Protocol { get; private set; }
    public int Port { get; private set; }
    public string? IpAddr { get; private set; }
    public bool DirectMode { get; private set; }
    public string PartialPath { get; private set; } = "/*";
    public bool UseOAuth { get; private set; }
    public string Audience { get; private set; } = "";
    public bool UsesRetryAfter { get; private set; } = true;

    // Cached path matching properties for performance
    private readonly bool _isCatchAllPath;
    private readonly string? _normalizedPartialPath;
    private readonly bool _isWildcardPath;
    private readonly string? _wildcardPrefix;

    private struct ParsedConfig
    {
      public string Host;
      public string ProbePath;
      public bool DirectMode;
      public string? IpAddr;
      public string PartialPath;
      public bool UseOAuth;
      public string Audience;
      public bool UsesRetryAfter;
    }

    public string Url => new UriBuilder(Protocol, IpAddr ?? Host, Port).Uri.AbsoluteUri;
    public string ProbeUrl => WebUtility.UrlDecode(new UriBuilder(Protocol, IpAddr ?? Host, Port, ProbePath).Uri.AbsoluteUri);

    /// <summary>
    /// Tracks status for circuit breaker
    /// </summary>
    public void TrackStatus(int code, bool wasException) => _circuitBreaker.TrackStatus(code, wasException);

    /// <summary>
    /// Checks if this host's circuit breaker is in failed state
    /// </summary>
    public bool CheckFailedStatus() => _circuitBreaker.CheckFailedStatus();


    /// <summary>
    /// Initializes the HostConfig with required dependencies
    /// </summary>
    public static void Initialize(BackendTokenProvider tokenProvider, ILogger logger, IServiceProvider serviceProvider)
    {
      _tokenProvider = tokenProvider;
      _logger = logger;
      _serviceProvider = serviceProvider;
    }

    // Can pass in hostname  and probepath
    // or
    // hostname=host=<host:port>;probe=<path>;mode=<direct|apim|>;ipaddress=<ipaddress>;path=<partialpath>;usesretryafter=<true|false>
    /// <summary>
    /// Constructs a BackendHostConfig from a hostname and optional probe path.
    /// </summary>
    public HostConfig(string hostname, string? probepath = "", string? audience = "")
    {
      // Get CircuitBreaker instance from DI container
      if (_serviceProvider == null)
        throw new InvalidOperationException("HostConfig service provider not initialized. Call SetServiceProvider first.");
      
      _circuitBreaker = (ICircuitBreaker)_serviceProvider.GetService(typeof(ICircuitBreaker))
        ?? throw new InvalidOperationException("ICircuitBreaker service not registered in DI container.");
      
      _logger?.LogInformation("[CONFIG] Configuring backend host: {hostname}", hostname);
      var parsed = TryParseConfig(hostname, probepath, audience);

      // If host does not have a protocol, add one
      string hostForUri = parsed.Host;
      if (!hostForUri.StartsWith("http://") && !hostForUri.StartsWith("https://"))
      {
        hostForUri = "https://" + hostForUri;
      }

      _circuitBreaker.ID = hostForUri;

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
      UsesRetryAfter = parsed.UsesRetryAfter;

      // Pre-compute path matching properties for performance
      var trimmedPath = PartialPath?.Trim();
      _isCatchAllPath = string.IsNullOrEmpty(trimmedPath) || trimmedPath == "/" || trimmedPath == "/*";

      if (!_isCatchAllPath)
      {
        _normalizedPartialPath = trimmedPath!.TrimStart('/');
        _isWildcardPath = _normalizedPartialPath.EndsWith("/*");
        _wildcardPrefix = _isWildcardPath ? _normalizedPartialPath.Substring(0, _normalizedPartialPath.Length - 2) : null;
      }

      if (DirectMode)
      {
        _logger?.LogInformation("[CONFIG] ✓ Direct host configured: {Host} | Path: {PartialPath}", Host, PartialPath);
        probepath = String.Empty;
      }
      else
      {
        _logger?.LogInformation("[CONFIG] ✓ APIM  host configured: {Host} | Probe: /{ProbePath}", Host, ProbePath);
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
        Audience = audience ?? "",
        UsesRetryAfter = true
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
            case "usemi":
              result.UseOAuth = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
              break;
            case "audience":
              result.Audience = kvp.Value;
              break;
            case "useretryafter":
            case "retryafter":
              result.UsesRetryAfter = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Determines if this host supports the given request path based on its PartialPath configuration.
    /// This method is used for specific path matching only - catch-all paths are handled separately.
    /// - Exact paths like "/api" match exactly and as prefixes (e.g., "/api/c1/foo")
    /// - Paths ending with "/*" like "/api/*" match as prefixes
    /// </summary>
    /// <param name="requestPath">The request path to check against this host's PartialPath</param>
    /// <returns>True if this host supports the request path, false otherwise</returns>
    public bool SupportsPath(string requestPath)
    {
      // Skip catch-all paths - these are handled separately in FilterHostsByPath
      if (_isCatchAllPath)
      {
        return false;
      }
Console.WriteLine($"HostConfig: Checking path support. Host: {Host} | PartialPath: {PartialPath} | RequestPath: {requestPath}");
      // Normalize request path for comparison
      var normalizedRequestPath = requestPath.TrimStart('/');

      // If host path ends with /*, treat it as a prefix match
      if (_isWildcardPath)
      {
        return string.IsNullOrEmpty(_wildcardPrefix) || normalizedRequestPath.StartsWith(_wildcardPrefix, StringComparison.OrdinalIgnoreCase);
      }

      // Exact path match
      if (normalizedRequestPath.Equals(_normalizedPartialPath, StringComparison.OrdinalIgnoreCase))
      {

        return true;
      }

      // Prefix match: "/api" should match "/api/c1/foo" 
      // Check if request path starts with host path followed by '/' or is exactly the host path
      if (!string.IsNullOrEmpty(_normalizedPartialPath))
      {
        var hostPathWithSlash = _normalizedPartialPath.TrimEnd('/') + "/";
        return normalizedRequestPath.StartsWith(hostPathWithSlash, StringComparison.OrdinalIgnoreCase);
      }

      return false;
    }

  }
}
