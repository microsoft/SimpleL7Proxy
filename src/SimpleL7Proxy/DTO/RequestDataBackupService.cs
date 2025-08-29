using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy.BlobStorage;

namespace SimpleL7Proxy.DTO
{
    public class RequestDataBackupService : IRequestDataBackupService
    {
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<RequestDataBackupService> _logger;

        public RequestDataBackupService(IBlobWriter blobWriter, ILogger<RequestDataBackupService> logger)
        {
            _logger = logger;
            _logger.LogDebug("Backup Service starting");
            _blobWriter = blobWriter;
        }

        public async Task<RequestDataDtoV1?> RestoreAsync(string blobname)
        {
            try
            {
                using Stream stream = await _blobWriter.ReadBlobAsStreamAsync(Constants.Server, blobname);
                var streamReader = new StreamReader(stream);
                var json = await streamReader.ReadToEndAsync();
                return RequestDataConverter.DeserializeWithVersionHandling(json);
            }
            catch (BlobWriterException)
            {
                throw;
            }
        }

        public async Task BackupAsync(RequestData requestData)
        {
            var operation = "Creating blob";

            try
            {
                _logger.LogDebug($"Backing up request {requestData.Guid}");
                using Stream stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(Constants.Server, requestData.Guid.ToString());
                using var writer = new BufferedStream(stream);
                operation = "Serializing request data";
                var dto = new RequestDataDtoV1(requestData);
                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    WriteIndented = false
                });
                var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

                _logger.LogDebug($"Backing up request {requestData.Guid} with size {jsonBytes.Length} bytes:   Content: {json}");
                operation = "Writing to blob";
                await writer.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                await writer.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error occurred while {operation}: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> DeleteBackupAsync(string blobname)
        {
            try
            {
                _logger.LogDebug($"Deleting backup for blob {blobname}");
                await _blobWriter.DeleteBlobAsync(Constants.Server, blobname);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error occurred while deleting backup for blob {blobname}: {ex.Message}");
                return false;
            }
        }
    }
}