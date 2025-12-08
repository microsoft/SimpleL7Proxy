using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

using SimpleL7Proxy;
using SimpleL7Proxy.BlobStorage;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.ServiceBus;
using Shared.RequestAPI.Models;
// using SimpleL7Proxy.BackupAPI;

using System.Data.Common;

namespace SimpleL7Proxy.Proxy
{
    /// <summary>
    /// Represents an asynchronous worker that performs a task and disappears after completion.
    /// Review DISPOSAL_ARCHITECTURE.MD in the root for details on disposal flow
    /// </summary>
    public class AsyncWorker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private int _beginStartup = 0; // 0 = not started, 1 = started, -1 = abort startup
        TaskCompletionSource<bool> _taskCompletionSource = new TaskCompletionSource<bool>();
        //private int _completed = 0; // 0 = not completed, 1 = completed
        private RequestData _requestData { get; set; }
        private string _headerBlobUri { get; set; } = "";
        private string _dataBlobUri { get; set; } = "";
        private Stream? _hos { get; set; } = null!;
        private string _userId { get; set; } = "";
        private readonly IBlobWriter _blobWriter;
        private readonly ILogger<AsyncWorker> _logger;
        private readonly IRequestDataBackupService _requestBackupService;
        // private readonly IBackupAPIService _backupAPIService;
        public  bool ShouldReprocess { get; set; } = false; 
        public string ErrorMessage { get; set; } = "";
        string dataBlobName = "";
        string headerBlobName = "";
        private int AsyncTimeout;
        private readonly bool _generateSasTokens;
        private static readonly JsonSerializerOptions SerializeOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // This prevents URL encoding of & characters

        };

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncWorker"/> class.
        /// </summary>
        /// <param name="data">The request data.</param>
        /// <param name="blobWriter">The blob writer instance.</param>
        /// <param name="logger">The logger instance.</param>
        public AsyncWorker(RequestData data, int AsyncTriggerTimeout, IBlobWriter blobWriter, ILogger<AsyncWorker> logger, IRequestDataBackupService requestBackupService)//, IBackupAPIService backupAPIService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _requestData = data ?? throw new ArgumentNullException(nameof(data));
            _blobWriter = blobWriter ?? throw new ArgumentNullException(nameof(blobWriter));
            _requestBackupService = requestBackupService ?? throw new ArgumentNullException(nameof(requestBackupService));
            // _backupAPIService = backupAPIService ?? throw new ArgumentNullException(nameof(backupAPIService));
            _userId = data.profileUserId;
            AsyncTimeout = AsyncTriggerTimeout;

            // Determine if SAS tokens should be generated based on user profile config
            _generateSasTokens = data.AsyncClientConfig?.GenerateSasTokens ?? false;

            _logger.LogTrace("[AsyncWorker:{Guid}] Initializing - UserId: {UserId}, Timeout: {Timeout}ms, GenerateSAS: {GenerateSAS}", 
                data.Guid, data.profileUserId, AsyncTriggerTimeout, _generateSasTokens);
            if (!data.runAsync)
            {
                throw new ArgumentException("AsyncWorker can only be used for async requests.");
            }

            _cancellationTokenSource = new CancellationTokenSource();

        }

        /// <summary>
        /// Initializes the blob client asynchronously. This must be called after construction.
        /// </summary>
        /// <returns>A task that represents the asynchronous initialization operation.</returns>
        public async Task<bool> InitializeAsync()
        {
            var result = await _blobWriter.InitClientAsync(_userId, _requestData.BlobContainerName).ConfigureAwait(false);
            if (!result)
            {
                ErrorMessage = "Failed to initialize BlobWriter for AsyncWorker.";
                throw new InvalidOperationException("Failed to initialize BlobWriter for AsyncWorker.");
            }
            return result;
        }

        // Marks the worker as started immediately
        // Updates the request status to "ReProcessing"
        // Re-initializes the blob client
        // Creates new blob streams for both data and headers
        // Sets up output streams
        // Updates the backup API status
        // Regenerates SAS tokens for access
        // Sets TaskCompletionSource to signal successful restoration

        // Different from StartAsync():
        // Doesn't wait for the trigger timeout
        // Doesn't send a 202 response back to client (since this is a rehydration)
        // Immediately marks as started
        // Sets status as ReProcessing instead of AsyncProcessing
        // Creates new blobs rather than using existing ones

        public async Task PrepareResponseStreamsAsync(bool isBackground = false)
        {
            _beginStartup = 1; // mark as started

            _logger.LogInformation("[AsyncWorker:{Guid}] Restoring async worker - MID: {MID}, Background: {IsBackground}", 
                _requestData.Guid, _requestData.MID, isBackground);
            var operation = "Re-Initialize";
            try
            {
                await InitializeAsync().ConfigureAwait(false);

                operation = "Set Blob Names";
                // Set blob names without creating blobs yet (lazy creation)
                SetBlobNames(isBackground);
                
                // Generate base blob URIs (OAuth will handle authentication - no SAS tokens)
                _dataBlobUri = _blobWriter.GetBlobUri(_userId, dataBlobName);
                _headerBlobUri = _blobWriter.GetBlobUri(_userId, headerBlobName);
                
                _logger.LogDebug("[AsyncWorker:{Guid}] Base blob URIs configured - OAuth authentication required", _requestData.Guid);

                if (!isBackground)
                {
                    _requestData.RequestAPIStatus = RequestAPIStatusEnum.ReProcessing;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Failed during {operation}: {ex.Message}";
                _logger.LogError(ex, "[AsyncWorker:{Guid}] Restore failed during {Operation}", _requestData.Guid, operation);

                ProxyEvent eventData = new()
                {
                    Type = EventType.Exception,
                    ["Error"] = ErrorMessage,
                    ["Operation"] = operation,
                    Exception = ex
                };

                eventData.SendEvent();
                throw;
            }

            _logger.LogInformation("[AsyncWorker:{Guid}] Restore completed successfully - DataBlob: {DataBlobName}, HeaderBlob: {HeaderBlobName}", 
                _requestData.Guid, dataBlobName, headerBlobName);
            _taskCompletionSource.TrySetResult(true);
        }

        /// <summary>
        /// Initializes the async worker for background checks WITHOUT creating blobs.
        /// Blobs will be created lazily when first written to.
        /// </summary>
        public async Task InitializeForBackgroundCheck()
        {
            _beginStartup = 1; // Mark as started to prevent StartAsync() from running
            
            _logger.LogInformation("[AsyncWorker:{Guid}] Initializing for background check - MID: {MID}", 
                _requestData.Guid, _requestData.MID);
            
            try
            {
                await InitializeAsync().ConfigureAwait(false);
                
                // Use the same helper as other flows for consistency
                SetBlobNames(isBackground: true);
                
                // Always use OAuth (consistent with StartAsync and PrepareResponseStreamsAsync)
                _dataBlobUri = _blobWriter.GetBlobUri(_userId, dataBlobName);
                _headerBlobUri = _blobWriter.GetBlobUri(_userId, headerBlobName);
                
                _logger.LogDebug("[AsyncWorker:{Guid}] Base blob URIs configured - OAuth authentication required", _requestData.Guid);
                
                _logger.LogInformation("[AsyncWorker:{Guid}] Background check initialized - Blobs will be created on-demand - DataBlob: {DataBlob}, HeaderBlob: {HeaderBlob}", 
                    _requestData.Guid, dataBlobName, headerBlobName);
                _taskCompletionSource.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AsyncWorker:{Guid}] Failed to initialize for background check", _requestData.Guid);
                ErrorMessage = $"Failed to initialize for background check: {ex.Message}";
                throw;
            }
        }

        /// <summary>
        /// Sets blob names for the async request without creating the blobs.
        /// </summary>
        /// <param name="isBackground">Whether this is for background response (uses different blob naming).</param>
        private void SetBlobNames(bool isBackground = false)
        {
            dataBlobName = _requestData.Guid.ToString();
            if (isBackground)
            {
                dataBlobName += "-BackgroundResponse";
            }
            headerBlobName = dataBlobName + "-Headers";
            
            _logger.LogTrace("[AsyncWorker:{Guid}] Blob names set - Data: {DataBlob}, Header: {HeaderBlob}", 
                _requestData.Guid, dataBlobName, headerBlobName);
        }

        /// <summary>
        /// Creates the user data and header blobs for async response storage.
        /// </summary>
        /// <param name="isBackground">Whether this is for background response (uses different blob naming).</param>
        /// <returns>A tuple containing the data stream and header stream.</returns>
        private async Task<(Stream dataStream, Stream headerStream)> CreateUserBlobsAsync(bool isBackground = false)
        {
            dataBlobName = _requestData.Guid.ToString();
            if (isBackground)
            {
                dataBlobName += "-BackgroundResponse";
            }
            headerBlobName = dataBlobName + "-Headers";

            Console.WriteLine($"[BLOB-TRACE] AsyncWorker.CreateUserBlobs | Action: CreateBlobs | Guid: {_requestData.Guid} | UserId: {_userId} | DataBlob: {dataBlobName} | HeaderBlob: {headerBlobName} | IsBackground: {isBackground} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            
            _logger.LogTrace("[AsyncWorker:{Guid}] Creating user blobs - Data: {DataBlob}, Header: {HeaderBlob}", 
                _requestData.Guid, dataBlobName, headerBlobName);

            // Create both blobs in parallel
            var dataStreamTask = _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, dataBlobName);
            var headerStreamTask = _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, headerBlobName);

            await Task.WhenAll(dataStreamTask, headerStreamTask).ConfigureAwait(false);

            var dataStream = await dataStreamTask;
            var headerStream = await headerStreamTask;

            Console.WriteLine($"[BLOB-TRACE] AsyncWorker.CreateUserBlobs | Action: CreateBlobs-Complete | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");

            return (dataStream, headerStream);
        }

        /// <summary>
        /// Generates and configures SAS URIs or base blob URIs for the created blobs.
        /// Optionally adds headers to the HTTP response context.
        /// </summary>
        /// <param name="addToResponseHeaders">Whether to add the URIs to the HTTP response headers.</param>
        private async Task ConfigureBlobUrisAsync(bool addToResponseHeaders = false)
        {
            if (_generateSasTokens)
            {
                try
                {
                    _logger.LogDebug("[AsyncWorker:{Guid}] Generating SAS tokens for blobs", _requestData.Guid);
                    _dataBlobUri = await _blobWriter.GenerateSasTokenAsync(_userId, dataBlobName, TimeSpan.FromSeconds(_requestData.AsyncBlobAccessTimeoutSecs));
                    _headerBlobUri = await _blobWriter.GenerateSasTokenAsync(_userId, headerBlobName, TimeSpan.FromSeconds(_requestData.AsyncBlobAccessTimeoutSecs));
                    _logger.LogTrace("[AsyncWorker:{Guid}] SAS tokens generated successfully", _requestData.Guid);
                    
                    if (addToResponseHeaders && _requestData.Context != null)
                    {
                        _requestData.Context.Response.Headers.Add("x-Data-Blob-SAS-URI", _dataBlobUri);
                        _requestData.Context.Response.Headers.Add("x-Header-Blob-SAS-URI", _headerBlobUri);
                    }
                }
                catch (Exception sasEx)
                {
                    _logger.LogError(sasEx, "[AsyncWorker:{Guid}] Failed to create SAS token", _requestData.Guid);
                    ErrorMessage = "Failed to create SAS token: " + sasEx.Message;
                    throw;
                }
            }
            else
            {
                _logger.LogDebug("[AsyncWorker:{Guid}] SAS token generation skipped - providing base blob URIs", _requestData.Guid);
                _dataBlobUri = _blobWriter.GetBlobUri(_userId, dataBlobName);
                _headerBlobUri = _blobWriter.GetBlobUri(_userId, headerBlobName);
                
                if (addToResponseHeaders && _requestData.Context != null)
                {
                    _requestData.Context.Response.Headers.Add("x-Data-Blob-URI", _dataBlobUri);
                    _requestData.Context.Response.Headers.Add("x-Header-Blob-URI", _headerBlobUri);
                }
            }
        }

        /// <summary>
        /// Gets or creates the data output stream lazily. Only creates the blob when first accessed.
        /// </summary>
        /// <returns>The output stream for writing response data.</returns>
        public async Task<Stream> GetOrCreateDataStreamAsync()
        {
            if (_requestData.OutputStream == null)
            {
                Console.WriteLine($"[BLOB-TRACE] AsyncWorker.GetOrCreateDataStream | Action: LazyCreate | Guid: {_requestData.Guid} | DataBlob: {dataBlobName} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                
                try
                {
                    var dataStream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, dataBlobName);
                    _requestData.OutputStream = new BufferedStream(dataStream);
                    
                    Console.WriteLine($"[BLOB-TRACE] AsyncWorker.GetOrCreateDataStream | Action: Created | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AsyncWorker:{Guid}] Failed to create data stream", _requestData.Guid);
                    throw;
                }
            }
            return _requestData.OutputStream;
        }

        /// <summary>
        /// Starts the worker if it has not already been started.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task StartAsync()
        {
            try
            {

                _logger.LogTrace("[AsyncWorker:{Guid}] Starting with {Timeout}ms delay - UserId: {UserId}, MID: {MID}", 
                    _requestData.Guid, AsyncTimeout, _userId, _requestData.MID);
                // wait state... can be cancelled by Terminate
                if (AsyncTimeout > 10)
                {
                    await Task.Delay(AsyncTimeout, _cancellationTokenSource.Token).ConfigureAwait(false);
                }

                _logger.LogTrace("[AsyncWorker:{Guid}] Delay complete, initializing async processing", _requestData.Guid);
                //_logger.LogInformation($"AsyncWorker: Delayed for {AsyncTimeout} ms");
                // Atomically set to running (1) only if not started (0)
                if (Interlocked.CompareExchange(ref _beginStartup, 1, 0) == 0)
                {

                    _requestData.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessing;
                    _logger.LogInformation("[AsyncWorker:{Guid}] Async processing triggered - Status: {Status}", 
                        _requestData.Guid, _requestData.SBStatus);
                    var operation = "Initialize";
                    try
                    {
                        _requestData.RequestAPIStatus = RequestAPIStatusEnum.New;

                        _logger.LogTrace("[AsyncWorker:{Guid}] Calling InitializeAsync", _requestData.Guid);
                        await InitializeAsync().ConfigureAwait(false);
                        _logger.LogTrace("[AsyncWorker:{Guid}] InitializeAsync completed", _requestData.Guid);
                        
                        operation = "Set Blob Names";
                        // Only set blob names, don't create blobs yet (lazy creation for better performance)
                        SetBlobNames(isBackground: false);
                        
                        // Generate base blob URIs (OAuth will handle authentication - no SAS tokens)
                        _dataBlobUri = _blobWriter.GetBlobUri(_userId, dataBlobName);
                        _headerBlobUri = _blobWriter.GetBlobUri(_userId, headerBlobName);
                        
                        _logger.LogDebug("[AsyncWorker:{Guid}] Base blob URIs configured - OAuth authentication required", _requestData.Guid);

                        operation = "Backup Request";
                        // Backup the request data
                        await UpdateBackup().ConfigureAwait(false);

                    }
                    catch (Exception ex)
                    {
                        ErrorMessage = $"Failed during {operation}: {ex.Message}";
                        _logger.LogError(ex, "[AsyncWorker:{Guid}] Failed during {Operation}", 
                            _requestData.Guid, operation);

                        ProxyEvent eventData = new()
                        {
                            Type = EventType.Exception,
                            ["Error"] = ErrorMessage,
                            ["Operation"] = operation,
                            Exception = ex
                        };

                        eventData.SendEvent();

                        _taskCompletionSource.TrySetResult(false);
                        return;
                    }

                    AsyncMessage Statusmessage = new()
                    {
                        Status = 202,
                        Message = "Your request has been accepted for async processing. The final result will be available at the blob URIs. Use OAuth for authentication.",
                        MID = _requestData.MID,
                        UserId = _requestData.UserID,
                        Guid = _requestData.Guid.ToString(),
                        DataBlobUri = _dataBlobUri,
                        HeaderBlobUri = _headerBlobUri,
                        Timestamp = DateTime.UtcNow
                    };

                    try
                    {
                        _logger.LogDebug("[AsyncWorker:{Guid}] Writing 202 Accepted response to client", _requestData.Guid);
                        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Statusmessage, SerializeOptions) + "\n");

                        _requestData.Context!.Response.StatusCode = 202;
                        _requestData.Context.Response.Headers.Add("x-Data-Blob-URI", _dataBlobUri);
                        _requestData.Context.Response.Headers.Add("x-Header-Blob-URI", _headerBlobUri);
                        await _requestData.Context.Response.OutputStream.WriteAsync(message).ConfigureAwait(false);
                        await _requestData.Context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                        _requestData.Context.Response.Close();
                        _logger.LogDebug("[AsyncWorker:{Guid}] 202 response written and connection closed", _requestData.Guid);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogWarning(writeEx, "[AsyncWorker:{Guid}] Failed to write 202 response (client may have disconnected)", 
                            _requestData.Guid);
                        //proxyEventData["x-Status"] = "Network Error";
                        // Client disconnected?
                    }

                    _logger.LogInformation("[AsyncWorker:{Guid}] Async worker started successfully - DataBlob: {DataBlobUri}, HeaderBlob: {HeaderBlobUri}", 
                        _requestData.Guid, _dataBlobUri, _headerBlobUri);
                    _taskCompletionSource.TrySetResult(true); // Set the task completion source to indicate that the worker has started
                }
                else
                {
                    _logger.LogDebug("[AsyncWorker:{Guid}] Startup already in progress or completed", _requestData.Guid);
                    // Worker has already started, do nothing
                }


            }
            catch (ObjectDisposedException)
            {
                _taskCompletionSource.TrySetResult(false); // Set the task completion source to indicate that the worker was cancelled
            }
            catch (TaskCanceledException)
            {
                // Timer was cancelled by TryTerminate, do nothing
                _taskCompletionSource.TrySetResult(false); // Set the task completion source to indicate that the worker was cancelled
            }
            finally
            {
                // Dispose of the cancellation token source
                _cancellationTokenSource.Dispose();
            }

        }

        public Task UpdateBackup()
        {
            return _requestBackupService.BackupAsync(_requestData);
        }

        /// <summary>
        /// Writes HTTP headers to the blob storage asynchronously with retry logic.
        /// </summary>
        /// <param name="status">The HTTP status code to write.</param>
        /// <param name="headers">The HTTP headers to write.</param>
        /// <returns>True if headers were successfully written; otherwise, false.</returns>
        public async Task<bool> WriteHeaders(HttpStatusCode status, WebHeaderCollection headers)
        {
            const int MaxRetryAttempts = 5;
            const int BaseRetryDelayMs = 500;

            for (int attempt = 0; attempt < MaxRetryAttempts; attempt++)
            {
                try
                {
                    // Create or recreate the stream if needed
                    if (_hos == null)
                    {
                        Console.WriteLine($"[BLOB-TRACE] AsyncWorker.WriteHeaders | Action: RecreateStream | Guid: {_requestData.Guid} | UserId: {_userId} | HeaderBlob: {headerBlobName} | Attempt: {attempt + 1}/{MaxRetryAttempts} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                        
                        var stream = await _blobWriter.CreateBlobAndGetOutputStreamAsync(_userId, headerBlobName)
                            .ConfigureAwait(false);

                        if (stream == null)
                        {
                            _logger.LogError("Failed to create header stream on attempt {Attempt}", attempt + 1);
                            await Task.Delay(GetBackoffDelay(attempt, BaseRetryDelayMs)).ConfigureAwait(false);
                            continue;
                        }

                        _hos = stream;
                        Console.WriteLine($"[BLOB-TRACE] AsyncWorker.WriteHeaders | Action: RecreateStream-Complete | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    }

                    // Convert WebHeaderCollection to Dictionary<string, string> for proper JSON serialization
                    var headersDictionary = new Dictionary<string, string>();
                    foreach (string headerName in headers.AllKeys)
                    {
                        headersDictionary[headerName] = headers[headerName] ?? "";
                    }

                    // Prepare the message
                    var headerMessage = new AsyncHeaders
                    {
                        Status = status.ToString(),
                        // Serialize headers to a string
                        Headers = headersDictionary,
                        UserId = _requestData.UserID,
                        MID = _requestData.MID,
                        Guid = _requestData.Guid.ToString(),
                        Timestamp = DateTime.UtcNow,
                        BlobUri = _dataBlobUri
                    };

                    // Serialize the message
                    byte[] serializedMessage = Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(headerMessage, SerializeOptions) + "\n");

                    // Write to the stream
                    using (var bufferStream = new BufferedStream(_hos))
                    {
                        await bufferStream.WriteAsync(serializedMessage).ConfigureAwait(false);
                        await bufferStream.FlushAsync().ConfigureAwait(false);
                        return true;
                    }
                }
                catch (OutOfMemoryException e)
                {
                    _logger.LogError("Out of memory while writing headers (attempt {Attempt}): {Message}", attempt + 1, e.Message);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    // Exponential backoff for memory issues
                    await Task.Delay(GetBackoffDelay(attempt, BaseRetryDelayMs, true)).ConfigureAwait(false);
                    await ResetStreamAsync().ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    _logger.LogError("IO error while writing headers (attempt {Attempt}): {Message}", attempt + 1, e.Message);

                    if (e.InnerException is ObjectDisposedException)
                    {
                        _logger.LogError("Stream was disposed, will recreate on next attempt");
                    }

                    await Task.Delay(GetBackoffDelay(attempt, BaseRetryDelayMs)).ConfigureAwait(false);
                    await ResetStreamAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to write headers (attempt {Attempt}): {Message}", attempt + 1, ex.Message);
                    _logger.LogTrace("Exception details: {Exception}", ex);

                    // Don't retry for general exceptions
                    await ResetStreamAsync().ConfigureAwait(false);
                    return false;
                }
            }

            _logger.LogError("Failed to write headers after maximum retry attempts");
            return false;
        }

        /// <summary>
        /// Resets the header output stream by safely closing, disposing, and nullifying it.
        /// </summary>
        private async Task ResetStreamAsync()
        {
            if (_hos != null)
            {
                Console.WriteLine($"[BLOB-TRACE] AsyncWorker.ResetStream | Action: Reset | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                try
                {
                    await _hos.FlushAsync().ConfigureAwait(false);
                    _hos.Dispose();
                    Console.WriteLine($"[BLOB-TRACE] AsyncWorker.ResetStream | Action: Disposed | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine($"[BLOB-TRACE] AsyncWorker.ResetStream | Action: AlreadyDisposed | Guid: {_requestData.Guid} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    // Stream was already disposed, ignore
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BLOB-TRACE] AsyncWorker.ResetStream | Action: Error | Guid: {_requestData.Guid} | Error: {ex.Message} | Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    _logger.LogError("Error while resetting stream: {Message}", ex.Message);
                }
                finally
                {
                    _hos = null;
                }
            }
        }

        /// <summary>
        /// Calculates the appropriate backoff delay for retries.
        /// </summary>
        /// <param name="attempt">The current attempt number (0-based).</param>
        /// <param name="baseDelayMs">The base delay in milliseconds.</param>
        /// <param name="useExponential">Whether to use exponential backoff instead of linear.</param>
        /// <returns>The delay time in milliseconds.</returns>
        private static int GetBackoffDelay(int attempt, int baseDelayMs, bool useExponential = false)
        {
            return useExponential
                ? (int)Math.Pow(2, attempt) * baseDelayMs
                : baseDelayMs * (attempt + 1);
        }


        /// <summary>
        /// Synchronizes with the worker's lifecycle by either terminating it before startup or waiting for completion.
        /// If the worker hasn't started yet, this method will cancel it. If it has already started, 
        /// this method will wait for it to complete its initialization process.
        /// </summary>
        /// If there are issues with access, etc, this method may return <c>false</c>.
        /// <returns><c>true</c> if the operation completed successfully (either terminated or waited); otherwise, <c>false</c>.</returns>
        public async Task<bool> Synchronize()
        {
            // If it has not already entered startup, abort it and cancel the token
            if (Interlocked.CompareExchange(ref _beginStartup, -1, 0) == 0)
            {
                _cancellationTokenSource?.Cancel();

                // Async Worker has not started, Terminate it
                return true; // Worker was not started, so we terminated it
            }

            // Async Worker task has started setting up, wait for it to finish
            _requestData.AsyncTriggered = await _taskCompletionSource.Task.ConfigureAwait(false);

            if (!_requestData.AsyncTriggered)
            {
                await DisposeAsync().ConfigureAwait(false);

                return false; // Worker failed to start
            }
            
            return _requestData.AsyncTriggered; // Return the result of the worker's startup
        }

        /// <summary>
        /// Checks if the worker has been started.
        /// </summary>
        /// <returns><c>true</c> if the worker has been started; otherwise, <c>false</c>.</returns>
        public bool IsStarted()
        {
            return _beginStartup == 1;
        }

        public async Task AbortAsync()
        {
            if (Interlocked.CompareExchange(ref _beginStartup, -1, 0) == 0)
            {
                // unlikely to occur
                _logger.LogWarning("[AsyncWorker:{Guid}] Worker aborted before startup", _requestData.Guid);
                _cancellationTokenSource?.Cancel();

            }

            // should be always happening
            _requestData.RequestAPIStatus = RequestAPIStatusEnum.NeedsReprocessing;
            _logger.LogInformation("[AsyncWorker:{Guid}] Worker aborted - Status set to NeedsReprocessing", _requestData.Guid);
            //     // _backupAPIService.UpdateStatus(_requestAPIDocument);
            // }
            // else
            // {
            //     // unlikely to occur
            //     _logger.LogError("Worker was started but no RequestAPIDocument was found to update.");
            // }

            await UpdateBackup();            
            await DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {

            // if (_requestAPIDocument != null && sendCompletedStatus)
            // {
            //     _requestAPIDocument.status = RequestAPIStatusEnum.Completed;
            //     _backupAPIService.UpdateStatus(_requestAPIDocument);
            // }


            // remove backup
            // if (!ShouldReprocess) {
            //     _logger.LogCritical($"AsyncWorker: Deleting backup for blob {_requestData.Guid}");
            //     await _blobWriter.DeleteBlobAsync(Constants.Server, _requestData.Guid.ToString()).ConfigureAwait(false);
            // }

            // Dispose managed resources
            await ResetStreamAsync().ConfigureAwait(false);

            // Cancel any ongoing operations
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Cancellation token source was already disposed, ignore
            }

            // Clear any large object references
            _requestData = null!;

            // Suppress finalization
            GC.SuppressFinalize(this);
        }
    }

}