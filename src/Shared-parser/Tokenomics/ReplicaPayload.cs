namespace SimpleL7Proxy.Tokenomics;

public sealed class ReplicaPayload
{
    /// <summary>Batch ids declared by the upload's manifest line, in order.</summary>
    public IReadOnlyList<string> BatchIds { get; init; } = Array.Empty<string>();

    /// <summary>The complete raw body, retained for the rollup iterator to parse.</summary>
    public string Body { get; init; } = string.Empty;
}