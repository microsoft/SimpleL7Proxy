using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>
/// Reads the complete upload body once and parses only the <c>BatchIds:</c> manifest line.
/// CSV sections are left in <see cref="ReplicaPayload.Body"/> for the rollup iterator.
/// </summary>
public sealed class ReplicaPayloadReader
{
    private readonly PipeReader _reader;

    public ReplicaPayloadReader(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<ReplicaPayload> ReadAsync(CancellationToken cancellationToken = default)
    {
        // Buffer the whole content-length body, then decode once.
        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (result.IsCompleted)
            {
                var body = Decode(buffer);
                _reader.AdvanceTo(buffer.End);
                await _reader.CompleteAsync().ConfigureAwait(false);
                return new ReplicaPayload { BatchIds = ExtractBatchIds(body), Body = body };
            }

            // Keep the whole buffer until the body completes.
            _reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    private static string Decode(ReadOnlySequence<byte> buffer) =>
        buffer.IsSingleSegment
            ? Encoding.UTF8.GetString(buffer.FirstSpan)
            : Encoding.UTF8.GetString(buffer.ToArray());

    private static IReadOnlyList<string> ExtractBatchIds(string body)
    {
        var start = 0;
        while (start < body.Length)
        {
            var newline = body.IndexOf('\n', start);
            var end = newline < 0 ? body.Length : newline;
            var line = body.AsSpan(start, end - start).Trim();

            if (line.StartsWith("BatchIds:", StringComparison.OrdinalIgnoreCase))
            {
                return ParseIds(line["BatchIds:".Length..]);
            }

            // Reached the batch sections without a manifest line.
            if (line.StartsWith("BatchId:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return Array.Empty<string>();
    }

    private static List<string> ParseIds(ReadOnlySpan<char> manifest)
    {
        var ids = new List<string>();
        foreach (var range in manifest.Split(','))
        {
            var id = manifest[range].Trim();
            if (!id.IsEmpty)
            {
                ids.Add(id.ToString());
            }
        }

        return ids;
    }
}