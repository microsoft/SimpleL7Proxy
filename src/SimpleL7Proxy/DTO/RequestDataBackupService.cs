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
                using Stream stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(Constants.Server, requestData.Guid.ToString());
                using var writer = new BufferedStream(stream);
                operation = "Serializing request data";
                var json = JsonSerializer.Serialize(requestData);
                var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

                Console.WriteLine($"Backing up request {requestData.Guid} with size {jsonBytes.Length} bytes:   Content: {json}");
                operation = "Writing to blob";
                await writer.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                await writer.FlushAsync();
            }
            catch (Exception ex) when (operation == "Creating blob")
            {
                Console.WriteLine($"Error occurred while {operation}: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (operation == "Serializing request data")
            {
                Console.WriteLine($"Error occurred while {operation}: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (operation == "Writing to blob")
            {
                Console.WriteLine($"Error occurred while {operation}: {ex.Message}");   
                throw;
            }
        }
    }
}