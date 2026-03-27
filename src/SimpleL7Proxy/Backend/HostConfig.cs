using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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
    private ICircuitBreaker? _circuitBreaker;
    public Guid Guid { get; } = Guid.NewGuid();
    private ParsedConfig ParsedConfig { get; set; }
    public string Audience => ParsedConfig.Audience;
    public bool DirectMode => ParsedConfig.DirectMode;
    public string Host => ParsedConfig.Host;
    public string Hostname => ParsedConfig.Hostname;
    public string? IpAddr => ParsedConfig.IpAddr;
    public string PartialPath => ParsedConfig.PartialPath;
    public string ProbePath => ParsedConfig.ProbePath;
    public string Processor => ParsedConfig.Processor;
    public bool StripPrefix => ParsedConfig.StripPrefix;
    public bool UseOAuth => ParsedConfig.UseOAuth;
    public bool UsesRetryAfter => ParsedConfig.UsesRetryAfter;
    public string Protocol { get; private set; }
    public int Port { get; private set; }
    // Cached path matching properties for performance
    private readonly bool _isCatchAllPath;
    private readonly string? _normalizedPartialPath;
    private readonly bool _isWildcardPath;
    private readonly string? _wildcardPrefix;

    public string Url => ParsedConfig.Host;
    public string ProbeUrl { get; set; }
    private string? _frozenHash;

    /// <summary>
    /// Computes a deterministic SHA256 hash over all configuration-relevant fields.
    /// Two <see cref="HostConfig"/> instances with identical configuration will
    /// produce the same hash regardless of runtime state (Guid, circuit breaker, etc.).
    /// </summary>
    public string Hash()
    {
        // Order must stay stable — append every ParsedConfig field.
        var sb = new StringBuilder(512);
        sb.Append(Audience).Append('|');
        sb.Append(DirectMode).Append('|');
        sb.Append(Host).Append('|');
        sb.Append(Hostname).Append('|');
        sb.Append(IpAddr ?? string.Empty).Append('|');
        sb.Append(PartialPath).Append('|');
        sb.Append(ProbePath).Append('|');
        sb.Append(Processor ?? string.Empty).Append('|');
        sb.Append(StripPrefix).Append('|');
        sb.Append(UseOAuth).Append('|');
        sb.Append(UsesRetryAfter);

        Span<byte> hashBytes = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), hashBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Snapshots the current <see cref="Hash"/> so it can be retrieved later
    /// via <see cref="FrozenHash"/> without recomputing.
    /// Call this once after construction when the configuration is final.
    /// </summary>
    public void FreezeHash() => _frozenHash = Hash();

    /// <summary>
    /// Returns the hash captured by <see cref="FreezeHash"/>, or <c>null</c>
    /// if <see cref="FreezeHash"/> has not been called yet.
    /// </summary>
    public string? FrozenHash => _frozenHash;

    /// <summary>
    /// Whether <see cref="Activate"/> has been called (circuit breaker created).
    /// </summary>
    public bool IsActivated => _circuitBreaker is not null;

    /// <summary>
    /// Whether this host has been marked for spin-down.
    /// A spinning-down host is no longer part of the active snapshot but may
    /// still be referenced by in-flight workers holding an old snapshot.
    /// Once all in-flight work drains, the host can be reclaimed by GC.
    /// </summary>
    public bool IsSpinningDown { get; private set; }

    /// <summary>
    /// UTC timestamp when <see cref="SpinDown"/> was called, or <c>null</c>
    /// if the host is still active.
    /// </summary>
    public DateTime? SpinDownTime { get; private set; }

    /// <summary>
    /// Marks this host for graceful spin-down. The circuit breaker stops
    /// accepting new status tracking, and the host will no longer be selected
    /// for new requests.  In-flight requests on old snapshots continue
    /// to completion.
    /// </summary>
    public void SpinDown()
    {
        if (IsSpinningDown) return;
        IsSpinningDown = true;
        SpinDownTime = DateTime.UtcNow;

        // Remove this host's circuit breaker from global counters.
        // Deregister() is itself idempotent, but we only reach here once
        // because of the IsSpinningDown guard above.
        _circuitBreaker?.Deregister();
    }

    /// <summary>
    /// Creates the circuit breaker and other expensive runtime resources.
    /// Call this after the dedup check — only for configs that will actually
    /// be used.  Safe to call multiple times (no-op after first call).
    /// </summary>
    public void Activate()
    {
        if (_circuitBreaker is not null) return;

        if (_serviceProvider == null)
            throw new InvalidOperationException("HostConfig service provider not initialized. Call Initialize first.");

        _circuitBreaker = _serviceProvider.GetService<ICircuitBreaker>()
            ?? throw new InvalidOperationException("ICircuitBreaker service not registered in DI container.");

        _circuitBreaker.ID = ParsedConfig.Host;
    }

    private ICircuitBreaker CircuitBreaker =>
        _circuitBreaker ?? throw new InvalidOperationException(
            $"HostConfig for '{Host}' has not been activated. Call Activate() before using circuit breaker.");

    /// <summary>
    /// Tracks status for circuit breaker
    /// </summary>
    public void TrackStatus(int code, bool wasFailure, string state) => CircuitBreaker.TrackStatus(code, wasFailure, state);

    /// <summary>
    /// Checks if this host's circuit breaker is in failed state
    /// </summary>
    public Task<bool> CheckFailedStatusAsync() => CircuitBreaker.CheckFailedStatusAsync();

    public string GetCircuitBreakerStatusString() => CircuitBreaker.GetCircuitBreakerStatusString();

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
    // Comma-delimited key/value pairs are also accepted.
    /// <summary>
    /// Constructs a BackendHostConfig from a hostname and optional probe path.
    /// </summary>
    public HostConfig(string hostname, string? probepath = "", string? ip = null, string? audience = "")
    {
      _logger?.LogDebug("[CONFIG] Configuring backend host: {hostname}", hostname);
      ParsedConfig = TryParseConfig(hostname, probepath, ip, audience);

      // parse the host, protocol and port
      Uri uri = new Uri(ParsedConfig.Host);
      Protocol = uri.Scheme;
      Port = uri.Port;

      // Pre-compute path matching properties for performance
      var trimmedPath = PartialPath?.Trim();
      _isCatchAllPath = string.IsNullOrEmpty(trimmedPath) || trimmedPath == "/" || trimmedPath == "/*";

      if (!DirectMode)
      {
        string hostOrIp = string.IsNullOrEmpty(IpAddr) ? Hostname : IpAddr;
        _logger?.LogDebug("Making probe url with Protocol: {Protocol} Host: {Host} Port: {Port} ProbePath: {ProbePath}",
            Protocol, hostOrIp, Port, ProbePath);
        ProbeUrl = WebUtility.UrlDecode(new UriBuilder(Protocol, hostOrIp, Port, ProbePath).Uri.AbsoluteUri);
      }
      else
      {
        ProbeUrl = String.Empty;
      }

      if (!_isCatchAllPath)
      {
        _normalizedPartialPath = trimmedPath!.TrimStart('/');
        _isWildcardPath = _normalizedPartialPath.EndsWith("/*");
        _wildcardPrefix = _isWildcardPath ? _normalizedPartialPath.Substring(0, _normalizedPartialPath.Length - 2) : null;
      }

      if (DirectMode)
      {
        _logger?.LogDebug("[CONFIG] ✓ Direct host configured: {Host} | Path: {PartialPath}", Host, PartialPath);
        probepath = String.Empty;
      }
      else
      {
        _logger?.LogDebug("[CONFIG] ✓ APIM  host configured: {Host} | Probe: /{ProbePath}", Host, ProbePath);
      }
    }


    private static ParsedConfig TryParseConfig(string hostname, string? probepath, string? ip, string? audience = "")
    /// <summary>
    /// Parses a backend configuration string into a ParsedConfig struct.
    /// </summary>
    {
      var result = new ParsedConfig
      {
        Host = hostname,
        ProbePath = probepath?.TrimStart('/') ?? "echo/resource?param1=sample",
        DirectMode = false,
        IpAddr = ip ?? "",
        PartialPath = "/",
        StripPrefix = true,
        UseOAuth = false,
        Audience = audience ?? "",
        UsesRetryAfter = true
      };

      if (hostname.Contains(';') || hostname.Contains(','))
      {
        var configDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in hostname.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
          var splitIndex = part.IndexOf('=');
          if (splitIndex <= 0 || splitIndex >= part.Length - 1)
            throw new UriFormatException($"Invalid backend host configuration part: {part}");

          var key = part[..splitIndex];
          var value = part[(splitIndex + 1)..];
          configDict[key] = value;
        }

        foreach (var kvp in configDict)
        {
          switch (kvp.Key.ToLowerInvariant())
          {
            case "audience":
              result.Audience = kvp.Value;
              break;
            case "host":
              result.Host = NormalizeHostUrl(kvp.Value);
              break;
            case "ipaddress":
              result.IpAddr = kvp.Value;
              break;
            case "mode":
              result.DirectMode = kvp.Value.Equals("direct", StringComparison.OrdinalIgnoreCase);
              break;
            case "path":
              result.PartialPath = kvp.Value;
              break;
            case "probe":
              result.ProbePath = kvp.Value;
              break;
            case "processor":
              result.Processor = kvp.Value;
              break;
            case "stripprefix":
            case "strippathprefix":
              result.StripPrefix = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
              break;
            case "useoauth":
            case "usemi":
              result.UseOAuth = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
              break;
            case "useretryafter":
            case "retryafter":
              result.UsesRetryAfter = kvp.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
              break;
            default:
              throw new UriFormatException($"Invalid backend host configuration key: {kvp.Key}");
          }

          if (result.DirectMode)                // For DirectMode, ignore probe path
          {
            result.ProbePath = "";
          }
        }
      }

      // try an parse the hostname if for non direct mode hosts.
      if (!result.DirectMode)
      {
        result.Hostname = result.Host;
      } else 
      {
        Console.WriteLine($"Direct mode host detected: {result.Host}");
        result.Hostname = new Uri(result.Host).Host;
        if (string.IsNullOrEmpty(result.Processor))
        {
          result.Processor = StreamProcessor.StreamProcessorFactory.DEFAULT_PROCESSOR;
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
    /// Normalizes a host URL by ensuring it has a protocol and removing trailing slashes.
    /// </summary>
    /// <param name="hostValue">The raw host value from configuration</param>
    /// <returns>Normalized host URL</returns>
    private static string NormalizeHostUrl(string hostValue)
    {
      ArgumentException.ThrowIfNullOrWhiteSpace(hostValue, nameof(hostValue));

      ReadOnlySpan<char> normalized = hostValue.AsSpan().Trim();

      // Ensure protocol is present
      if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
          !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
      {
        return string.Concat("https://", normalized.TrimEnd('/'));
      }

      // Remove trailing slashes using span
      return normalized.TrimEnd('/').ToString();
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
    /// Determines if this host supports the given request path and returns the path with prefix removed.
    /// This method is used for specific path matching only - catch-all paths are handled separately.
    /// - Exact paths like "/api" match exactly and as prefixes (e.g., "/api/c1/foo")
    /// - Paths ending with "/*" like "/api/*" match as prefixes
    /// </summary>
    /// <param name="requestPath">The request path to check against this host's PartialPath</param>
    /// <param name="strippedPath">Output: the request path with the matched prefix removed</param>
    /// <returns>True if this host supports the request path, false otherwise</returns>
    public PathMatchResult SupportsPath(string requestPath)
    {
        // Skip catch-all paths - these are handled separately in FilterHostsByPath
        if (_isCatchAllPath)
        {
            return PathMatchResult.NoMatch(requestPath);
        }

        // Split path and query using span to avoid allocations
        ReadOnlySpan<char> pathSpan = requestPath.AsSpan();
        int queryIndex = pathSpan.IndexOf('?');
        ReadOnlySpan<char> path = queryIndex >= 0 ? pathSpan.Slice(0, queryIndex) : pathSpan;
        ReadOnlySpan<char> query = queryIndex >= 0 ? pathSpan.Slice(queryIndex) : ReadOnlySpan<char>.Empty;
        
        // Normalize request path for comparison (trim leading slashes)
        ReadOnlySpan<char> normalizedPath = path.TrimStart('/');

        // If host path ends with /*, treat it as a prefix match
        if (_isWildcardPath)
        {
            if (string.IsNullOrEmpty(_wildcardPrefix) || 
                normalizedPath.StartsWith(_wildcardPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                // When StripPrefix is false, match but keep the original path
                if (!StripPrefix)
                {
                    return PathMatchResult.Match(requestPath);
                }
                // Strip the wildcard prefix
                if (!string.IsNullOrEmpty(_wildcardPrefix))
                {
                    var remaining = normalizedPath.Slice(_wildcardPrefix.Length).TrimStart('/');
                    return PathMatchResult.Match(string.Concat("/", remaining, query));
                }
                return PathMatchResult.Match(requestPath);
            }
            return PathMatchResult.NoMatch(requestPath);
        }

        // Exact path match
        if (normalizedPath.Equals(_normalizedPartialPath.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            if (!StripPrefix)
            {
                return PathMatchResult.Match(requestPath);
            }
            return PathMatchResult.Match(query.IsEmpty ? "/" : string.Concat("/", query));
        }

        // Prefix match: "/api" should match "/api/c1/foo" 
        if (!string.IsNullOrEmpty(_normalizedPartialPath))
        {
            var prefixSpan = _normalizedPartialPath.AsSpan();
            
            if (normalizedPath.StartsWith(prefixSpan, StringComparison.OrdinalIgnoreCase))
            {
                if (normalizedPath.Length == prefixSpan.Length || 
                    normalizedPath[prefixSpan.Length] == '/')
                {
                    if (!StripPrefix)
                    {
                        return PathMatchResult.Match(requestPath);
                    }
                    var remaining = normalizedPath.Slice(prefixSpan.Length).TrimStart('/');
                    return PathMatchResult.Match(string.Concat("/", remaining, query));
                }
            }
        }

        return PathMatchResult.NoMatch(requestPath);
    }
  }
}
