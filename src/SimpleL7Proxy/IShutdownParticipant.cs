namespace SimpleL7Proxy;

/// <summary>
/// Implemented by DI-registered services that need explicit cleanup during coordinated shutdown.
/// Services are discovered automatically via <c>IEnumerable&lt;IShutdownParticipant&gt;</c> —
/// no need to manually wire them into <see cref="CoordinatedShutdownService"/>.
///
/// To participate:
/// 1. Implement this interface on the service class.
/// 2. Add a forwarding registration in DI:
///    <c>services.AddSingleton&lt;IShutdownParticipant&gt;(sp =&gt; (IShutdownParticipant)sp.GetRequiredService&lt;IPrimaryInterface&gt;());</c>
/// </summary>
public interface IShutdownParticipant
{
    /// <summary>
    /// Lower values execute first. Use to express dependencies between participants.
    /// </summary>
    int ShutdownOrder { get; }

    /// <summary>
    /// Called during coordinated shutdown after all proxy workers have drained.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
