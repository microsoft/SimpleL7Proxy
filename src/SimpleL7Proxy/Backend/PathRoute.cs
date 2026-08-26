namespace SimpleL7Proxy.Backend;

using SimpleL7Proxy.Backend.Iterators;

/// <summary>
/// Unresolved named path configuration loaded from a <c>Path_*</c> setting.
/// </summary>
public sealed class PathRouteDefinition
{
  public string Name { get; }
  public string Prefix { get; }
  public IReadOnlyList<string> HostKeys { get; }
  public bool StripPrefix { get; }
  /// <summary>Overrides the global iteration mode for this route when set.</summary>
  public IterationModeEnum? IterationMode { get; }
  /// <summary>Overrides the global LoadBalancing:MultiPass:MaxAttempts for this route when set.</summary>
  public int? MaxAttempts { get; }

  public PathRouteDefinition(
      string name,
      string prefix,
      IEnumerable<string> hostKeys,
      bool stripPrefix,
      int? maxAttempts = null,
      IterationModeEnum? iterationMode = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentNullException.ThrowIfNull(hostKeys);

    Name = name.Trim();
    Prefix = NormalizePrefix(prefix);
    HostKeys = hostKeys
        .Select(hostKey => hostKey.Trim())
        .Where(hostKey => hostKey.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    StripPrefix = stripPrefix;

    if (iterationMode.HasValue &&
        iterationMode.Value is not IterationModeEnum.SinglePass and not IterationModeEnum.MultiPass)
      throw new UriFormatException($"Path route '{Name}' iterationmode must be SinglePass or MultiPass.");
    IterationMode = iterationMode;

    if (maxAttempts is < 1)
      throw new UriFormatException($"Path route '{Name}' maxattempts must be a positive integer.");
    MaxAttempts = maxAttempts;

    if (HostKeys.Count == 0)
      throw new UriFormatException($"Path route '{Name}' must reference at least one host.");
  }

  internal string Signature =>
      $"{Name.ToUpperInvariant()}|{Prefix.ToUpperInvariant()}|{StripPrefix}|{IterationMode}|{MaxAttempts}|{string.Join(':', HostKeys.Select(key => key.ToUpperInvariant()))}";

  private static string NormalizePrefix(string prefix)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

    var normalized = prefix.Trim();
    if (normalized.Contains('?') || normalized.Contains('#'))
      throw new UriFormatException($"Path route prefix cannot contain a query or fragment: {prefix}");

    if (!normalized.StartsWith('/'))
      normalized = "/" + normalized;

    if (normalized.EndsWith("/*", StringComparison.Ordinal))
      normalized = normalized[..^2];

    normalized = normalized.TrimEnd('/');
    return normalized.Length == 0 ? "/" : normalized;
  }
}

/// <summary>
/// Resolved route whose host references point at one immutable host snapshot.
/// </summary>
public sealed class PathRoute
{
  public string Name { get; }
  public string Prefix { get; }
  public bool StripPrefix { get; }
  /// <summary>Overrides the global iteration mode for this route when set.</summary>
  public IterationModeEnum? IterationMode { get; }
  /// <summary>Overrides the global LoadBalancing:MultiPass:MaxAttempts for this route when set.</summary>
  public int? MaxAttempts { get; }
  public IReadOnlyList<HostConfig> ConfiguredHosts { get; }
  public IReadOnlyList<BaseHostHealth> DirectHosts { get; }
  public BaseHostHealth? GatewayHost { get; }
  public bool UsesGateway => GatewayHost is not null;

  internal PathRoute(
      PathRouteDefinition definition,
      IReadOnlyList<HostConfig> configuredHosts,
      IReadOnlyList<BaseHostHealth> directHosts,
      BaseHostHealth? gatewayHost)
  {
    Name = definition.Name;
    Prefix = definition.Prefix;
    StripPrefix = definition.StripPrefix;
    IterationMode = definition.IterationMode;
    MaxAttempts = definition.MaxAttempts;
    ConfiguredHosts = configuredHosts;
    DirectHosts = directHosts;
    GatewayHost = gatewayHost;
  }

  public PathMatchResult Match(string requestPath)
  {
    ArgumentNullException.ThrowIfNull(requestPath);

    var queryIndex = requestPath.IndexOf('?');
    var path = queryIndex >= 0 ? requestPath[..queryIndex] : requestPath;
    var query = queryIndex >= 0 ? requestPath[queryIndex..] : string.Empty;

    if (Prefix == "/")
      return PathMatchResult.Match(requestPath);

    if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ||
        (path.Length != Prefix.Length && path[Prefix.Length] != '/'))
    {
      return PathMatchResult.NoMatch(requestPath);
    }

    if (!StripPrefix)
      return PathMatchResult.Match(requestPath);

    var remaining = path[Prefix.Length..].TrimStart('/');
    return PathMatchResult.Match(string.Concat("/", remaining, query));
  }

  internal List<BaseHostHealth> GetCandidateHosts(int requestPriority)
  {
    var hasEligibleHost = requestPriority == Constants.AnyPriority
        ? ConfiguredHosts.Count > 0
        : ConfiguredHosts.Any(host => host.AcceptsPriority(requestPriority));

    if (UsesGateway)
      return hasEligibleHost ? [GatewayHost!] : [];

    return DirectHosts
        .Where(host => requestPriority == Constants.AnyPriority || host.Config.AcceptsPriority(requestPriority))
        .ToList();
  }
}

public readonly record struct PathRouteMatch(PathRoute Route, string ModifiedPath);