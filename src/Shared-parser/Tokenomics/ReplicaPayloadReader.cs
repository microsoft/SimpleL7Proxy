using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Text;

namespace SimpleL7Proxy.Tokenomics;

public sealed class ReplicaPayloadReader
{
    private readonly PipeReader _reader;

    public ReplicaPayloadReader(PipeReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<ReplicaPayload> ReadAsync(CancellationToken cancellationToken = default)
    {
        string? replicaId = null;
        var batches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentBatchId = null;
        StringBuilder? currentBatch = null;

        while (true)
        {
            ReadResult result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var lineBytes))
            {
                ProcessLine(
                GetLine(lineBytes),
                ref replicaId,
                ref currentBatchId,
                ref currentBatch,
                batches);
            }

            if (result.IsCompleted && !buffer.IsEmpty)
            {
                ProcessLine(
                    GetLine(buffer),
                    ref replicaId,
                    ref currentBatchId,
                    ref currentBatch,
                    batches);

                buffer = buffer.Slice(buffer.End);
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        if (currentBatchId is not null)
        {
            batches[currentBatchId] = currentBatch?.ToString() ?? string.Empty;
        }

        await _reader.CompleteAsync().ConfigureAwait(false);

        return new ReplicaPayload
        {
            ReplicaId = replicaId ?? string.Empty,
            Batches = batches
        };
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> lineBytes)
    {
        SequencePosition? position = buffer.PositionOf((byte)'\n');

        if (position is null)
        {
            lineBytes = default;
            return false;
        }

        lineBytes = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));

        return true;
    }

    private static string GetLine(ReadOnlySequence<byte> sequence)
    {
        return Encoding.UTF8.GetString(sequence.ToArray()).TrimEnd('\r');
    }

    private static void ProcessLine(
        string line,
        ref string? replicaId,
        ref string? currentBatchId,
        ref StringBuilder? currentBatch,
        Dictionary<string, string> batches)
    {
        line = line.TrimStart('\uFEFF');

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (replicaId is null &&
            line.StartsWith("ReplicaId:", StringComparison.OrdinalIgnoreCase))
        {
            replicaId = line["ReplicaId:".Length..].Trim();
            return;
        }

        if (line.StartsWith("BatchId:", StringComparison.OrdinalIgnoreCase))
        {
            // Save the previous batch before starting the next one.
            if (currentBatchId is not null)
            {
                batches[currentBatchId] =
                    currentBatch?.ToString() ?? string.Empty;
            }

            currentBatchId = line["BatchId:".Length..].Trim();
            currentBatch = new StringBuilder();
            return;
        }

        currentBatch?.AppendLine(line);
    }
}
