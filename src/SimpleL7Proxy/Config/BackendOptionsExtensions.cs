using System.Net;
using System.Net.Sockets;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Config;

/// <summary>
/// Extension methods for <see cref="BackendOptions"/> that provide a cleaner
/// calling syntax for configuration parsing operations.
/// </summary>
public static class BackendOptionsExtensions
{
    /// <summary>
    /// Applies a single configuration field from the environment dictionary to this
    /// <see cref="BackendOptions"/> instance, falling back to the corresponding
    /// default value when the environment variable is absent or set to the
    /// default placeholder.
    /// </summary>
    /// <param name="target">The <see cref="BackendOptions"/> instance to update.</param>
    /// <param name="env">Dictionary of environment/configuration key-value pairs.</param>
    /// <param name="defaults">A default <see cref="BackendOptions"/> instance providing fallback values.</param>
    /// <param name="envVar">The environment variable (dictionary key) to look up.</param>
    /// <param name="property">The name of the <see cref="BackendOptions"/> property to set.</param>
    public static void ApplyFieldFromEnv(this BackendOptions target, Dictionary<string, string> env, BackendOptions defaults, string envVar, string property)
    {
        ConfigParser.ApplyFieldFromEnv(env, target, defaults, envVar, property);
    }

    public static BackendOptions Apply(this BackendOptions defaults, Dictionary<string, string> dict)
    {
        return ConfigParser.ApplyEnv(dict, defaults);
    }

    /// <summary>
    /// Creates a new <see cref="BackendOptions"/> instance by applying environment variable overrides
    /// from the provided dictionary on top of the given defaults.
    /// </summary> 
    public static BackendOptions ApplyTo(BackendOptions defaults, Dictionary<string, string> dict)
    {
        return ConfigParser.ApplyEnv(dict, defaults);
    }
    /// <summary>
    /// Creates and assigns an <see cref="HttpClient"/> on this <see cref="BackendOptions"/>
    /// instance, configured from the transport-related properties (keep-alive, HTTP/2, SSL).
    /// </summary>
    public static void ConfigureHttpClient(this BackendOptions backendOptions)
    {
        var safeKeepAliveInitialDelaySecs = Math.Max(1, backendOptions.KeepAliveInitialDelaySecs);
        var safeKeepAlivePingIntervalSecs = Math.Max(1, backendOptions.KeepAlivePingIntervalSecs);

        var retryCount = Math.Max(1, backendOptions.KeepAliveIdleTimeoutSecs / safeKeepAlivePingIntervalSecs);
        var handler = CreateSocketsHandler(safeKeepAliveInitialDelaySecs, safeKeepAlivePingIntervalSecs, retryCount);

        if (backendOptions.EnableMultipleHttp2Connections)
        {
            handler.EnableMultipleHttp2Connections = true;
            handler.PooledConnectionLifetime = TimeSpan.FromSeconds(backendOptions.MultiConnLifetimeSecs);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(backendOptions.MultiConnIdleTimeoutSecs);
            handler.MaxConnectionsPerServer = backendOptions.MultiConnMaxConns;
            handler.ResponseDrainTimeout = TimeSpan.FromSeconds(backendOptions.KeepAliveIdleTimeoutSecs);
            Console.WriteLine("Multiple HTTP/2 connections enabled.");
        }
        else
        {
            handler.EnableMultipleHttp2Connections = false;
            Console.WriteLine("Multiple HTTP/2 connections disabled.");
        }

        // Configure SSL handling
        if (backendOptions.IgnoreSSLCert)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
            };
            Console.WriteLine("Ignoring SSL certificate validation errors.");
        }

        HttpClient client = new HttpClient(handler)
        {
            // set timeout to large to disable it at HttpClient level. Will use token cancellation for timeout instead.
            Timeout = Timeout.InfiniteTimeSpan
        };

        backendOptions.Client = client;
    }

    private static SocketsHttpHandler CreateSocketsHandler(int initialDelaySecs, int intervalSecs, int linuxRetryCount)
    {
        SocketsHttpHandler handler = new SocketsHttpHandler();
        handler.ConnectCallback = async (ctx, ct) =>
        {
            DnsEndPoint dnsEndPoint = ctx.DnsEndPoint;
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, dnsEndPoint.AddressFamily, ct).ConfigureAwait(false);
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                bool linuxKeepAliveConfigured = false;

                // Basic keep-alive setting - should work on all platforms
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // Windows-specific approach using IOControl
                        byte[] keepAliveValues = new byte[12];
                        BitConverter.GetBytes((uint)1).CopyTo(keepAliveValues, 0);           // Turn keep-alive on
                        BitConverter.GetBytes((uint)(initialDelaySecs * 1000)).CopyTo(keepAliveValues, 4);
                        BitConverter.GetBytes((uint)(intervalSecs * 1000)).CopyTo(keepAliveValues, 8);

                        s.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, initialDelaySecs);
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSecs);
                        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, linuxRetryCount);
                        linuxKeepAliveConfigured = true;
                    }
                }
                catch (Exception ex)
                {
                    ProxyEvent pe = new()
                    {
                        Type = EventType.Exception,
                        Exception = ex,
                        ["Message"] = "Failed to set TCP keep-alive parameters",
                        ["Host"] = dnsEndPoint.Host,
                        ["Port"] = dnsEndPoint.Port.ToString(),
                        ["InitialDelaySecs"] = initialDelaySecs.ToString(),
                        ["IntervalSecs"] = intervalSecs.ToString(),
                        ["LinuxRetryCount"] = linuxRetryCount.ToString(),
                        ["linuxKeepAliveConfigured"] = linuxKeepAliveConfigured.ToString()
                    };
                    pe.SendEvent();
                }

                // Connect to the endpoint
                await s.ConnectAsync(addresses, dnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(s, ownsSocket: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Socket connection error: {ex.Message}");
                s.Dispose();
                throw;
            }
        };

        return handler;
    }
}
