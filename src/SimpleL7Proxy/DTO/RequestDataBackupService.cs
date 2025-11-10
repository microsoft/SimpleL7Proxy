using System.Text;
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
            _logger.LogDebug("[INIT] BackupAPI Service starting");
            _blobWriter = blobWriter;
        }

        public async Task RestoreIntoAsync(RequestData rdata)
        {
            string blobname = rdata.Guid.ToString();
            
            try
            {
                // Console.WriteLine("RequestDataBackupService: Reading blob from " + Constants.Server + " with name " + blobname);
                using Stream stream = await _blobWriter.ReadBlobAsStreamAsync(Constants.Server, blobname);
                var streamReader = new StreamReader(stream);
                var json = await streamReader.ReadToEndAsync();
                var data = RequestDataConverter.DeserializeWithVersionHandling(json);

                if (data is null)
                {
                    _logger.LogInformation($"Blob {blobname} deserialized to null.");
                    throw new JsonException("Deserialized RequestDataDtoV1 is null");
                }

                _logger.LogDebug($" Reading into request {rdata.Guid}  URL: {rdata.FullURL}  UsedId: {rdata.UserID} ");

                data.PopulateInto(rdata);
                _logger.LogDebug($" After populate: Reading into request {rdata.Guid}  URL: {rdata.FullURL}  UsedId: {rdata.UserID} ");

                // read body bytes if present
                var bodyBlobName = blobname + ".body";
                if (await _blobWriter.BlobExistsAsync(Constants.Server, bodyBlobName))
                {
                    using Stream bodyStream = await _blobWriter.ReadBlobAsStreamAsync(Constants.Server, bodyBlobName);
                    var bodyStreamReader = new StreamReader(bodyStream);
                    var datastr = await bodyStreamReader.ReadToEndAsync();
                    rdata.setBody(Encoding.UTF8.GetBytes(datastr));
                }

                return;
            }
            catch (BlobWriterException e)
            {
                _logger.LogInformation($"Blob {blobname} error reading from blob.");
                Console.WriteLine(e.StackTrace);
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogInformation($"Blob {blobname} error deserializing json: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error occurred while restoring backup for blob {blobname}: {ex.Message}");
                throw;
            }
        }

        public async Task BackupAsync(RequestData requestData)
        {
            var operation = "Creating blob";

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
                await using (var stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(Constants.Server, requestData.Guid.ToString()))
                await using (var writer = new BufferedStream(stream))
                {
                    await writer.WriteAsync(jsonBytes, 0, jsonBytes.Length);
                    await writer.FlushAsync();
                }

                // Only write out the body bytes blob the first time.. The body does not change on retries 
                if (requestData.BodyBytes != null)
                {
                    var exists = await _blobWriter.BlobExistsAsync(Constants.Server, requestData.Guid.ToString() + ".body");
                    if (exists)
                    {
                        _logger.LogDebug($"Backup blob for body of request {requestData.Guid} already exists. Skipping write.");
                        return;
                    }

                    await using (var stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(Constants.Server, requestData.Guid.ToString() + ".body"))
                    await using (var writer = new BufferedStream(stream))
                    {
                        await writer.WriteAsync(requestData.BodyBytes, 0, requestData.BodyBytes.Length);
                        await writer.FlushAsync();
                    }

                }

                _logger.LogDebug($"Backup of request {requestData.Guid} completed successfully.");
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
                _logger.LogCritical($"RequestDataBackupService: Deleting backup for blob {blobname}");
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