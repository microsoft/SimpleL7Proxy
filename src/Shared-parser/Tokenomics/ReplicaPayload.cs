namespace SimpleL7Proxy.Tokenomics;

public sealed class ReplicaPayload
{
    public string ReplicaId { get; init; } = string.Empty;

    public Dictionary<string, string> Batches { get; init; } = new();
}
