//using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimpleL7Proxy.Backend;
using System.Net;
using System.Text.Json;
using SimpleL7Proxy.BlobStorage;

namespace SimpleL7Proxy.Storage;

public class StorageDbRequestStorageService : IRequestStorageService
{

    private readonly IBlobWriter _blobWriter;
    private readonly string _container;
    private readonly ILogger<StorageDbRequestStorageService> _logger;
    private readonly BackendOptions _options;
    private readonly bool _isEnabled;
    private const string clientName = "SimpleL7ProxyStorage";


    public StorageDbRequestStorageService(
        IOptions<BackendOptions> options,
        ILogger<StorageDbRequestStorageService> logger,
        IBlobWriter blobWriter)
    {
        _logger = logger;
        _options = options.Value;
        _isEnabled = _options.StorageDbEnabled;
        _blobWriter = blobWriter;
        _container = _options.StorageDbContainerName;

        if (_isEnabled)
        {
            if (string.IsNullOrEmpty(_container))
            {
                _logger.LogError("StorageDbContainerName is not set in BackendOptions.");
                throw new ArgumentException("StorageDbContainerName cannot be null or empty.");
            }

            if (_blobWriter == null)
            {
                _logger.LogError("BlobWriter is not initialized.");
                throw new ArgumentNullException(nameof(blobWriter), "BlobWriter cannot be null.");
            }

            if (!_blobWriter.InitClientAsync(clientName, _container).GetAwaiter().GetResult())
            {
                _logger.LogError("Failed to initialize BlobWriter for StorageDbRequestStorageService.");
                throw new InvalidOperationException("BlobWriter initialization failed.");
            }
        }
    }

    public async Task<bool> IsEnabledAsync()
    {
        return await Task.FromResult(_isEnabled);
    }

    public async Task<bool> StoreRequestAsync(RequestData request)
    {
        if (!_isEnabled || _container == null)
        {
            return false;
        }

        try
        {
            var requestDocument = new StorageRequestDocument
            {
                id = request.MID,
                RequestId = request.MID,
                UserID = request.UserID,
                Method = request.Method,
                Path = request.Path,
                FullURL = request.FullURL,
                Priority = request.Priority,
                Priority2 = request.Priority2,
                EnqueueTime = request.EnqueueTime,
                ExpiresAt = request.ExpiresAt,
                ExpireReason = request.ExpireReason,
                Headers = ConvertHeadersToDict(request.Headers),
                Status = "Enqueued",
                Guid = request.Guid.ToString(),
                _partitionKey = request.UserID
            };

            await _blobWriter.CreateBlobAndGetOutputStreamAsync(clientName, request.MID)
                .ContinueWith(async streamTask =>
                {
                    using var stream = await streamTask;
                    await JsonSerializer.SerializeAsync(stream, requestDocument);
                    await stream.FlushAsync();
                });
            _logger.LogDebug($"Stored request {request.MID} to CosmosDB");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to store request {request.MID} to CosmosDB: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeleteRequestAsync(RequestData request)
    {
        if (!_isEnabled || _container == null)
        {
            return false;
        }

        try
        {
            // Delete the blob
            await _blobWriter.DeleteBlobAsync(clientName, request.MID);
            _logger.LogDebug($"Deleted request {request.MID} from CosmosDB");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to delete request {request.MID} from CosmosDB: {ex.Message}");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {

        await Task.CompletedTask;
    }

    private static Dictionary<string, string> ConvertHeadersToDict(System.Collections.Specialized.NameValueCollection headers)
    {
        var headerDict = new Dictionary<string, string>();
        foreach (string? key in headers.AllKeys)
        {
            if (key != null)
            {
                headerDict[key] = headers[key] ?? "";
            }
        }
        return headerDict;
    }
}

public class StorageRequestDocument
{
    public string id { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string UserID { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string FullURL { get; set; } = "";
    public int Priority { get; set; }
    public int Priority2 { get; set; }
    public DateTime EnqueueTime { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string ExpireReason { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Status { get; set; } = "";
    public string Guid { get; set; } = "";
    public int ttl { get; set; } // CosmosDB TTL field
    public string _partitionKey { get; set; } = ""; // Explicit partition key field
}