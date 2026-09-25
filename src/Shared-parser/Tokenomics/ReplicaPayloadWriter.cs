namespace SimpleL7Proxy.Tokenomics;

using System.Text;

public static class ReplicaPayloadMaker
{
    private const char LF = '\n';

    public static string Make(
        string replicaId,
        IEnumerable<KeyValuePair<string, string>> batches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);

        var sb = new StringBuilder();

        sb.Append("ReplicaId: ")
          .Append(replicaId)
          .Append(LF);

        foreach (var (batchId, csv) in batches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

            sb.Append("BatchId: ")
              .Append(batchId)
              .Append(LF);

            // Normalize line endings.
            var csv2 = csv.ReplaceLineEndings("\n");

            sb.Append(csv2);

            if (!csv2.EndsWith('\n'))
            {
                sb.Append(LF);
            }

            // blank line between batches
            sb.Append(LF);
        }

        return sb.ToString();
    }
}