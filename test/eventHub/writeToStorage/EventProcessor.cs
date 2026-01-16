using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Microsoft.Extensions.Logging;

namespace EventHubToStorage;

/// <summary>
/// Represents an event data item to be processed
/// </summary>
public record EventData(string PartitionId, string Body, DateTimeOffset EnqueuedTime, long SequenceNumber);

/// <summary>
/// Processes events from Event Hub and writes them to storage using producer-consumer pattern
/// </summary>
public class EventProcessor
{
    private readonly EventHubConsumerClient _consumerClient;
    private readonly BlobWriter _blobWriter;
    private readonly ILogger? _logger;
    private ConcurrentBag<EventData> _eventList = new();

    public EventProcessor(EventHubConsumerClient consumerClient, BlobWriter blobWriter, ILogger? logger = null)
    {
        _consumerClient = consumerClient ?? throw new ArgumentNullException(nameof(consumerClient));
        _blobWriter = blobWriter ?? throw new ArgumentNullException(nameof(blobWriter));
        _logger = logger;
    }

    /// <summary>
    /// Starts processing events from all partitions with producer-consumer pattern
    /// </summary>
    public async Task ProcessEventsAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("Starting event processing...");
        
        var partitionIds = await _consumerClient.GetPartitionIdsAsync(cancellationToken);

        // Start consumer task (runs every minute)
        var consumerTask = Task.Run(() => ConsumerTaskAsync(cancellationToken), cancellationToken);

        // Start producer tasks (one per partition)
        var producerTasks = partitionIds.Select(partitionId => 
            Task.Run(() => ProducerTaskAsync(partitionId, cancellationToken), cancellationToken)).ToList();

        // Wait for all tasks
        await Task.WhenAll(producerTasks.Append(consumerTask));
    }

    /// <summary>
    /// Producer task: reads events from Event Hub and adds them to the list
    /// </summary>
    private async Task ProducerTaskAsync(string partitionId, CancellationToken cancellationToken)
    {
        Console.WriteLine($"[Producer-{partitionId}] Started reading from partition {partitionId}");

        try
        {
            await foreach (PartitionEvent partitionEvent in _consumerClient.ReadEventsFromPartitionAsync(
                partitionId,
                EventPosition.Latest,
                cancellationToken))
            {
                var eventBody = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());
                var eventData = new EventData(
                    partitionId,
                    eventBody,
                    partitionEvent.Data.EnqueuedTime,
                    partitionEvent.Data.SequenceNumber);

                _eventList.Add(eventData);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[Producer-{partitionId}] Cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"[Producer-{partitionId}] Error reading events");
            Console.WriteLine($"[Producer-{partitionId}] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Consumer task: runs every minute, drains the list and processes all events
    /// </summary>
    private async Task ConsumerTaskAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Consumer] Started batch processing (runs every 60 seconds)");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);

                // Atomically swap the list with a new empty one
                var oldList = Interlocked.Exchange(ref _eventList, new ConcurrentBag<EventData>());
                var batch = oldList.ToList();

                if (batch.Count == 0)
                {
                    Console.WriteLine("[Consumer] No events to process this cycle");
                    continue;
                }

                await ProcessBatchAsync(batch);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Consumer] Cancelled");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Consumer] Error processing batch");
            Console.WriteLine($"[Consumer] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Processes a batch of events
    /// </summary>
    private async Task ProcessBatchAsync(List<EventData> batch)
    {
        Console.WriteLine($"[Consumer] Batch contains {batch.Count} events");

        List<StorageRecord> storageRecords = StorageRecord.FromEventDataBatch(batch, "SimpleL7ProxyInstanceLogWriter");
        var json = JsonSerializer.Serialize(storageRecords);
        Console.WriteLine(json);

        // construct filename based on current timestamp :  <role>/<year>/<month>/<day>/<hour>/<minute>.json
        var timestamp = DateTimeOffset.UtcNow;
        var filename = $"logwriter/SimpleL7ProxyInstanceLogWriter/{timestamp:yyyy/MM/dd/HH}/{timestamp:mm}.json";

        // write out to blob storage
        await _blobWriter.WriteAsync(filename, json);

        // TODO: Add logic to write batch to blob storage via _blobWriter
        await Task.CompletedTask;
    }
}
