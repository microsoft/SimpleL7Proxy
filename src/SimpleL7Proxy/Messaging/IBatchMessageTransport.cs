using System.Threading;
using System.Threading.Tasks;

namespace SimpleL7Proxy.Messaging;

internal readonly record struct BatchMessageEnvelope(string Destination, string Payload);

internal interface IBatchMessageTransport<TBatch>
{
    Task OpenAsync(CancellationToken cancellationToken);
    ValueTask<TBatch> CreateBatchAsync(string destination, CancellationToken cancellationToken);
    bool TryAdd(TBatch batch, BatchMessageEnvelope message);
    int GetCount(TBatch batch);
    Task SendAsync(string destination, TBatch batch, CancellationToken cancellationToken);
    void DisposeBatch(TBatch batch);
    Task CloseAsync(CancellationToken cancellationToken);
}