namespace SimpleL7Proxy.Tokenomics;

using System.Text;

public static class ReplicaPayloadMaker
{
    private const char LF = '\n';

    public static string Make(
        IReadOnlyList<KeyValuePair<string, string>> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);

        var builder = new StringBuilder();

        // Keep the batch manifest on the second line while removing
        // replica identity from the request body.
        builder.Append("Tokenomics-Version: 1").Append(LF);
        builder.Append("BatchIds: ");

        for (var index = 0; index < batches.Count; index++)
        {
            var batchId = batches[index].Key;
            ValidateBatchId(batchId);

            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(batchId);
        }

        builder.Append(LF);

        foreach (var (batchId, csv) in batches)
        {
            ValidateBatchId(batchId);
            ArgumentNullException.ThrowIfNull(csv);

            builder.Append("BatchId: ")
                .Append(batchId)
                .Append(LF);

            var normalizedCsv = csv.ReplaceLineEndings("\n");
            builder.Append(normalizedCsv);

            if (!normalizedCsv.EndsWith('\n'))
            {
                builder.Append(LF);
            }

            builder.Append(LF);
        }

        return builder.ToString();
    }

    private static void ValidateBatchId(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);

        if (batchId.AsSpan().IndexOfAny(',', '\r', '\n') >= 0)
        {
            throw new ArgumentException(
                "Batch IDs cannot contain commas or line breaks.",
                nameof(batchId));
        }
    }
}