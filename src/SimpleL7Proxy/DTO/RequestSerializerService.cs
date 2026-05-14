using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.Async;
using SimpleL7Proxy.Async.BlobStorage;

namespace SimpleL7Proxy.DTO
{
    public class RequestSerializerService : IRequestSerializerService
    {
        private readonly IAsyncFileStore _requestStore;
        private readonly ILogger<RequestSerializerService> _logger;

        // Single-flight init for the Server backup container. The storage layer is
        // user-agnostic, so this service owns initialization of Constants.Server.
        private Task<bool>? _serverInitTask;
        private readonly object _serverInitLock = new();

        public RequestSerializerService(IAsyncFileStore requestStore, ILogger<RequestSerializerService> logger)
        {
            _logger = logger;
            _logger.LogDebug("[STARTUP] BackupAPI Service starting");
            _requestStore = requestStore;
        }

        private Task<bool> EnsureServerContainerInitializedAsync()
        {
            var existing = _serverInitTask;
            if (existing != null && !existing.IsFaulted && !existing.IsCanceled)
                return existing;

            lock (_serverInitLock)
            {
                if (_serverInitTask == null || _serverInitTask.IsFaulted || _serverInitTask.IsCanceled)
                {
                    _serverInitTask = _requestStore.InitializeClientAsync(Constants.Server);
                }
                return _serverInitTask;
            }
        }

        public async Task RestoreIntoAsync(RequestData rdata)
        {
            await EnsureServerContainerInitializedAsync().ConfigureAwait(false);
            string blobname = rdata.Guid.ToString();
            
            _logger.LogTrace($"[{rdata.Guid}] Restore Container: {Constants.Server}");
            
            try
            {
                // Console.WriteLine("RequestSerializerService: Reading blob from " + Constants.Server + " with name " + blobname);
                using Stream stream = await _requestStore.ReadBlobAsStreamAsync(Constants.Server, blobname);
                var data = await RequestDataConverter.DeserializeWithVersionHandlingAsync(stream);

                if (data is null)
                {
                    _logger.LogInformation($"[{rdata.Guid}] Blob {blobname} deserialized to null.");
                    throw new JsonException("Deserialized RequestDataDtoV1 is null");
                }

                _logger.LogDebug($"[{rdata.Guid}] Reading into request  URL: {rdata.FullURL}  UsedId: {rdata.UserID} ");

                data.PopulateInto(rdata);
                _logger.LogDebug($"[{rdata.Guid}] After populate: Reading into request  URL: {rdata.FullURL}  UsedId: {rdata.UserID} ");

                // read body bytes if present
                var bodyBlobName = blobname + ".body";
                if (await _requestStore.BlobExistsAsync(Constants.Server, bodyBlobName))
                {
                    //_logger.LogTrace($"[BLOB-TRACE] BackupService.RestoreIntoAsync | Action: ReadBody | Guid: {rdata.Guid} | Container: {Constants.Server} | Blob: {bodyBlobName} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    using Stream bodyStream = await _requestStore.ReadBlobAsStreamAsync(Constants.Server, bodyBlobName);
                    using var ms = new MemoryStream();
                    await bodyStream.CopyToAsync(ms);
                    rdata.setBody(ms.ToArray());
                    //_logger.LogTrace($"[BLOB-TRACE] BackupService.RestoreIntoAsync | Action: ReadBody-Complete | Guid: {rdata.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                }
                else if (rdata.Body == null)
                {
                    // No body blob exists — client never sent a request body.
                    // Set an empty body so ProxyToBackEndAsync doesn't throw ArgumentNullException.
                    rdata.setBody(Array.Empty<byte>());
                    _logger.LogInformation($"[{rdata.Guid}] No body blob found - client did not send a request body");
                }

                //_logger.LogTrace($"[BLOB-TRACE] BackupService.RestoreIntoAsync | Action: Complete | Guid: {rdata.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                return;
            }
            catch (BlobWriterException e)
            {
                _logger.LogInformation($"[{rdata.Guid}] Blob {blobname} error reading from blob.");
                _logger.LogError(e.StackTrace);
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogInformation($"[{rdata.Guid}] Blob {blobname} error deserializing json: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{rdata.Guid}] Error occurred while restoring backup for blob {blobname}: {ex.Message}");
                throw;
            }
        }

        public async Task BackupAsync(RequestData requestData)
        {
            await EnsureServerContainerInitializedAsync().ConfigureAwait(false);
            var operation = "Creating blob";

            //_logger.LogTrace($"[BLOB-TRACE] BackupService.BackupAsync | Action: Start | Guid: {requestData.Guid} | Container: {Constants.Server} | Blob: {requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");

            try
            {
                operation = "Serializing request data";

                _logger.LogDebug("BackupAPI: Backing up request {guid}", requestData.Guid);
                var dto = new RequestDataDtoV1(requestData);
                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    WriteIndented = false
                });
                var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

                operation = "Writing to blob";
                await _requestStore.WriteAsync(Constants.Server, requestData.Guid.ToString(), jsonBytes).ConfigureAwait(false);
                _logger.LogTrace($"[{requestData.Guid}] BackupService.BackupAsync | Action: Written | Guid: {requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");

                // Only write out the body bytes blob the first time.. The body does not change on retries 
                if (requestData.BodyBytes != null)
                {
                    var bodyBlobName = requestData.Guid.ToString() + ".body";
                    var exists = await _requestStore.BlobExistsAsync(Constants.Server, bodyBlobName);
                    if (exists)
                    {
                        _logger.LogTrace($"[{requestData.Guid}] BackupService.BackupAsync | Action: BodyExists-Skip | Guid: {requestData.Guid} | Blob: {bodyBlobName} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                        _logger.LogDebug($"[{requestData.Guid}] Backup blob for body of request {requestData.Guid} already exists. Skipping write.");
                        return;
                    }

                    _logger.LogTrace($"[{requestData.Guid}] BackupService.BackupAsync | Action: WriteBody | Guid: {requestData.Guid} | Container: {Constants.Server} | Blob: {bodyBlobName} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    await _requestStore.WriteAsync(Constants.Server, bodyBlobName, requestData.BodyBytes).ConfigureAwait(false);
                    _logger.LogTrace($"[{requestData.Guid}] BackupService.BackupAsync | Action: WriteBody-Complete | Guid: {requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");

                }
                
                _logger.LogTrace($"[{requestData.Guid}] BackupService.BackupAsync | Action: Complete | Guid: {requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");

                _logger.LogDebug($"[{requestData.Guid}] Backup of request {requestData.Guid} completed successfully.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{requestData.Guid}] Error occurred while {operation}: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> DeleteBackupAsync(string blobname)
        {
            try
            {
                await EnsureServerContainerInitializedAsync().ConfigureAwait(false);
                _logger.LogCritical($"[{blobname}] RequestSerializerService: Deleting backup for blob {blobname}");
                await _requestStore.DeleteBlobAsync(Constants.Server, blobname);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[{blobname}] RequestSerializerService: Error occurred while deleting backup for blob {blobname}: {ex.Message}");
                return false;
            }
        }
    }
}