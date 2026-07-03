namespace SimpleL7Proxy.Backend;

/// <summary>
/// Indicates that a backend host configuration was parsed successfully but is disabled.
/// </summary>
public sealed class HostConfigDisabledException : Exception
{
  public HostConfigDisabledException(string host)
      : base($"Backend host is disabled: {host}")
  {
    Host = host;
  }

  public string Host { get; }
}