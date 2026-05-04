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
using SimpleL7Proxy.Async;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.DTO;
using SimpleL7Proxy.Async.ServiceBus;
using Shared.RequestAPI.Models;
// using SimpleL7Proxy.BackupAPI;

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
        private IRequestDataBackupService? _backupService;
        public  bool ShouldReprocess { get; set; } = false; 
        public string ErrorMessage { get; set; } = "";
        string dataBlobName = "";
        string headerBlobName = "";
        private int AsyncTimeout;
        private readonly bool _generateSasTokens;
        private static TemplateLoader _messages = null!;
        private static IAsyncFileStore _fileStore = null!;
        private static IAsyncStreamingStore _streamingStore = null!;
        private static ILogger<AsyncWorker> _logger = null!;
        private static ProbeServer _probeServer = null!;
        private static ProxyConfig _options = null!;
        private static JsonSerializerOptions SerializeOptions=null!;

        /// <summary>
        /// Initializes static dependencies. Call this once at application startup before creating instances.
        /// Two stores are required: <paramref name="fileStore"/> handles small one-shot blobs through
        /// the BlobWriteQueue (headers, status); <paramref name="streamingStore"/> handles potentially-
        /// gigabyte response bodies by streaming directly to storage (bypassing the queue).
        /// </summary>
        public static void Initialize(IAsyncFileStore fileStore, 
            IAsyncStreamingStore streamingStore, 
            ILogger<AsyncWorker> logger, 
            TemplateLoader messages,
            ProxyConfig options, 
            ProbeServer probeService)
        {
            _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
            _streamingStore = streamingStore ?? throw new ArgumentNullException(nameof(streamingStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _probeServer = probeService ?? throw new ArgumentNullException(nameof(probeService));

            SerializeOptions = new()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // This prevents URL encoding of & characters
            };
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncWorker"/> class.
        /// </summary>
        /// <param name="data">The request data.</param>
        /// <param name="requestStore">The async request store instance.</param>
        /// <param name="logger">The logger instance.</param>
        public AsyncWorker(RequestData data, int AsyncTriggerTimeout)
        {
            _requestData = data ?? throw new ArgumentNullException(nameof(data));
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
        /// Convenience constructor that pulls construction-time dependencies from a shared
        /// <see cref="AsyncWorkerContext"/>.  Preferred over the multi-arg ctor — callers
        /// inject the singleton context once and forward it on each <c>new AsyncWorker</c>.
        /// </summary>
        public AsyncWorker(RequestData data, int AsyncTriggerTimeout, AsyncWorkerContext context)
            : this(data, AsyncTriggerTimeout)
        {
            _messages = context.Messages;
            _backupService = context.BackupService;
        }

        /// <summary>
        /// Initializes the blob client asynchronously. This must be called after construction.
        /// </summary>
        /// <returns>A task that represents the asynchronous initialization operation.</returns>
        public async Task<bool> InitializeAsync()
        {
            // BlobWriter cache (BlobWriter._containerClients) is static, so initializing on either
            // store warms the container client for the streaming store too.
            var result = await _fileStore.InitializeClientAsync(_requestData.BlobContainerName).ConfigureAwait(false);
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
                (_dataBlobUri, _headerBlobUri) = _fileStore.GetBlobUriPair(_requestData.BlobContainerName, dataBlobName, headerBlobName);
                
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
                (_dataBlobUri, _headerBlobUri) = _fileStore.GetBlobUriPair(_requestData.BlobContainerName, dataBlobName, headerBlobName);
                
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
        /// Generates and configures SAS URIs or base blob URIs for the created blobs.
        /// Optionally adds headers to the HTTP response context.
        /// </summary>
        /// <param name="addToResponseHeaders">Whether to add the URIs to the HTTP response headers.</param>
        private async Task ConfigureBlobUrisAsync(bool addToResponseHeaders = false)
        {
            // if (_generateSasTokens)
            // {
            //     try
            //     {
            //         _logger.LogDebug("[AsyncWorker:{Guid}] Generating SAS tokens for blobs", _requestData.Guid);
            //         (_dataBlobUri, _headerBlobUri) = await _fileStore.GenerateSasTokenPairAsync(_requestData.BlobContainerName, dataBlobName, headerBlobName, TimeSpan.FromSeconds(_requestData.AsyncBlobAccessTimeoutSecs)).ConfigureAwait(false);
            //         _logger.LogTrace("[AsyncWorker:{Guid}] SAS tokens generated successfully", _requestData.Guid);
                    
            //         if (addToResponseHeaders && _requestData.Context != null)
            //         {
            //             _requestData.Context.Response.Headers.Add("x-Data-Blob-SAS-URI", _dataBlobUri);
            //             _requestData.Context.Response.Headers.Add("x-Header-Blob-SAS-URI", _headerBlobUri);
            //         }
            //     }
            //     catch (Exception sasEx)
            //     {
            //         _logger.LogError(sasEx, "[AsyncWorker:{Guid}] Failed to create SAS token", _requestData.Guid);
            //         ErrorMessage = "Failed to create SAS token: " + sasEx.Message;
            //         throw;
            //     }
            // }
            // else
            // {
                // _logger.LogDebug("[AsyncWorker:{Guid}] SAS token generation skipped - providing base blob URIs", _requestData.Guid);
                (_dataBlobUri, _headerBlobUri) = _fileStore.GetBlobUriPair(_requestData.BlobContainerName, dataBlobName, headerBlobName);
                
                if (addToResponseHeaders && _requestData.Context != null)
                {
                    _requestData.Context.Response.Headers.Add("x-Data-Blob-URI", _dataBlobUri);
                    _requestData.Context.Response.Headers.Add("x-Header-Blob-URI", _headerBlobUri);
                }
            //}
        }

        /// <summary>
        /// Gets or creates the data output stream lazily. Only creates the blob when first accessed.
        /// </summary>
        /// <returns>The output stream for writing response data.</returns>
        public async Task<Stream> GetResponseDataStreamAsync()
        {
            if (_requestData.OutputStream == null)
            {
                //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.GetResponseDataStream | Action: LazyCreate | Guid: {Guid} | DataBlob: {DataBlob}", _requestData.Guid, dataBlobName);
                
                try
                {
                    // Data path is potentially gigabytes — go straight to BlobClient.OpenWriteAsync.
                    // The SDK transfer buffer (~4 MiB by default, tunable via AsyncStreamingBufferSizeBytes)
                    // caps memory regardless of total size.
                    //
                    // Intentionally do NOT pass the worker CTS token: cancelling an in-flight blob
                    // upload would leave a partial/empty blob and break correctness for the client
                    // that already received the 202 with this blob URI. Writes must run to completion
                    // even during shutdown.
                    _requestData.OutputStream = await _streamingStore.OpenWriteStreamAsync(_requestData.BlobContainerName, dataBlobName, CancellationToken.None);
                    
                    //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.GetResponseDataStream | Action: Created | Guid: {Guid}", _requestData.Guid);
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
                    // during this time the downstream request is processing
                    await Task.Delay(AsyncTimeout, _cancellationTokenSource.Token).ConfigureAwait(false);
                }

                _logger.LogTrace("[AsyncWorker:{Guid}] Delay complete, initializing async processing", _requestData.Guid);
                //_logger.LogInformation($"AsyncWorker: Delayed for {AsyncTimeout} ms");

                // Atomically set to running (1) only if not started (0)  [ ITETCOBO:  aboted or ACTIVE !! ]
                if ( _probeServer.BlobQueueDepth < _options.AsyncBlobMaxQueue && Interlocked.CompareExchange(ref _beginStartup, 1, 0) == 0)
                {

                    _requestData.SBStatus = ServiceBusMessageStatusEnum.AsyncProcessing;

                    try {
                        // update the TTL based on the AsyncTTLSecs
                        _requestData.CalculateExpiration(_options.AsyncTTLSecs, _options.TTLHeader);
                    }
                    catch ( ProxyErrorException ex) {
                        // This should not happen as the header was already validated when the request was received
                        _logger.LogError(ex, "[AsyncWorker:{Guid}] Failed to calculate expiration", _requestData.Guid);
                        throw;
                    }

                    _logger.LogInformation("[AsyncWorker:{Guid}] Async processing triggered - Status: {Status}", 
                        _requestData.Guid, _requestData.SBStatus);
                    var operation = "Initialize";
                    try
                    {
                        _requestData.RequestAPIStatus = RequestAPIStatusEnum.New;

                        await InitializeAsync().ConfigureAwait(false);
                        
                        operation = "Set Blob Names";
                        // Only set blob names, don't create blobs yet (lazy creation for better performance)
                        SetBlobNames(isBackground: false);
                        
                        // Generate base blob URIs (OAuth will handle authentication - no SAS tokens)
                        (_dataBlobUri, _headerBlobUri) = _fileStore.GetBlobUriPair(_requestData.BlobContainerName, dataBlobName, headerBlobName);
                        
                        _logger.LogDebug("[AsyncWorker:{Guid}] Base blob URIs configured - OAuth authentication required", _requestData.Guid);

                        operation = "Backup Request";
                        // Backup the request data
                        await PersistRequestStateAsync().ConfigureAwait(false);

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
                            ["StackTrace"] = ex.StackTrace ?? string.Empty,
                            Exception = ex
                        };

                        eventData.SendEvent();

                        _taskCompletionSource.TrySetResult(false);
                        return;
                    }

                    AsyncMessage Statusmessage = _messages.GetMergedMessage(
                            AsyncResponseTypeEnum.Welcome,
                            _requestData.Guid.ToString(),
                            _requestData.MID,
                            _requestData.UserID,
                            _dataBlobUri,
                            _headerBlobUri);

                    // Timestamp is always "now" — overwrite whatever the template carried.
                    Statusmessage.Timestamp = DateTime.UtcNow;

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
                        
                        // CRITICAL: Clear the OutputStream after sending 202 response
                        // The client connection is now closed, so the original OutputStream is invalid.
                        // GetResponseDataStreamAsync() checks if OutputStream is null to decide whether
                        // to create a new blob stream. Without this, it would return the closed client
                        // stream instead of creating a blob stream, causing data to be lost.
                        _requestData.OutputStream = null;
                        
                        _logger.LogDebug("[AsyncWorker:{Guid}] 202 response written and connection closed", _requestData.Guid);
                    }
                    catch (Exception writeEx)
                    {
                        _logger.LogWarning(writeEx, "[AsyncWorker:{Guid}] Failed to write 202 response (client may have disconnected)", 
                            _requestData.Guid);
                        //proxyEventData["x-Status"] = "Network Error";
                        // Client disconnected?
                        
                        // Even on error, clear the OutputStream - the client is disconnected anyway
                        _requestData.OutputStream = null;
                    }

                    // _logger.LogInformation("[AsyncWorker:{Guid}] Async worker started successfully - DataBlob: {DataBlobUri}, HeaderBlob: {HeaderBlobUri}", 
                    //     _requestData.Guid, _dataBlobUri, _headerBlobUri);
                    _taskCompletionSource.TrySetResult(true); // Set the task completion source to indicate that the worker has started
                }
                else
                {
                    _requestData.runAsync = false;
                    _logger.LogDebug("[AsyncWorker:{Guid}] Startup already in progress or completed or blob queue depth exceeds maximum threshold", _requestData.Guid);
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

        public Task PersistRequestStateAsync()
        {
            return _backupService?.BackupAsync(_requestData) ?? Task.CompletedTask;
        }

        /// <summary>
        /// Waits for all queued blob write operations (data + headers) to be physically
        /// written to Azure Storage. Call this BEFORE sending a "Completed" status so the
        /// client does not try to read the blob before it exists.
        /// </summary>
        public async Task WaitForBlobWritesAsync(CancellationToken cancellationToken = default)
        {
            // Data stream is a raw SDK OpenWriteAsync stream — staged blocks are committed on
            // Dispose, so we must dispose here (before sending Completed) to ensure the blob is
            // visible. Subsequent dispose attempts in cleanup paths are no-ops via the catch.
            if (_requestData?.OutputStream != null)
            {
                try
                {
                    await _requestData.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await _requestData.OutputStream.DisposeAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { }
                _requestData.OutputStream = null;
            }

            // Header stream goes through the BlobWriteQueue — wait for the enqueued operation
            // to land in storage.
            await _fileStore.CompleteWriteStreamAsync(_hos, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes HTTP headers to the blob storage. The underlying QueuedBlobStream buffers
        /// the payload and enqueues it on FlushAsync; the BlobWriteQueue worker performs the
        /// actual upload with SDK-level retry on transient failures. No local retry needed.
        /// </summary>
        /// <param name="status">The HTTP status code to write.</param>
        /// <param name="headers">The HTTP headers to write.</param>
        /// <returns>True if the payload was successfully enqueued; otherwise, false.</returns>
        public async Task<bool> SaveResponseHeadersAsync(HttpStatusCode status, WebHeaderCollection headers)
        {
            try
            {
                if (_hos == null)
                {
                    // Headers are small and one-shot — go through the queued/UploadAsync path.
                    _hos = await _fileStore.OpenWriteStreamAsync(_requestData.BlobContainerName, headerBlobName)
                        .ConfigureAwait(false);
                }

                var headersDictionary = new Dictionary<string, string>(headers.Count);
                foreach (string headerName in headers.AllKeys)
                {
                    headersDictionary[headerName] = headers[headerName] ?? "";
                }

                var headerMessage = new AsyncHeaders
                {
                    Status    = status.ToString(),
                    Headers   = headersDictionary,
                    UserId    = _requestData.UserID,
                    MID       = _requestData.MID,
                    Guid      = _requestData.Guid.ToString(),
                    Timestamp = DateTime.UtcNow,
                    BlobUri   = _dataBlobUri
                };

                byte[] serializedMessage = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(headerMessage, SerializeOptions) + "\n");

                await _hos.WriteAsync(serializedMessage).ConfigureAwait(false);
                await _hos.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AsyncWorker:{Guid}] Failed to write headers - Blob: {HeaderBlob} - Type: {ExceptionType}",
                    _requestData.Guid, headerBlobName, ex.GetType().FullName);
                await ResetStreamAsync().ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// Resets the header output stream by safely closing, disposing, and nullifying it.
        /// </summary>
        private async Task ResetStreamAsync()
        {
            // Reset header output stream
            if (_hos != null)
            {
                //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetStream | Action: Reset | Guid: {Guid}", _requestData.Guid);
                try
                {
                    await _hos.FlushAsync().ConfigureAwait(false);
                    _hos.Dispose();
                    //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetStream | Action: Disposed | Guid: {Guid}", _requestData.Guid);
                }
                catch (ObjectDisposedException)
                {
                    //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetStream | Action: AlreadyDisposed | Guid: {Guid}", _requestData.Guid);
                    // Stream was already disposed, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[BLOB-TRACE] AsyncWorker.ResetStream | Action: Error | Guid: {Guid} | Error: {ErrorMessage}", 
                        _requestData.Guid, ex.Message);
                }
                finally
                {
                    _hos = null;
                }
            }
            
            // Reset data output stream - CRITICAL: must flush and close to commit blob data
            if (_requestData?.OutputStream != null)
            {
                //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetDataStream | Action: Reset | Guid: {Guid}", _requestData.Guid);
                try
                {
                    await _requestData.OutputStream.FlushAsync().ConfigureAwait(false);
                    await _requestData.OutputStream.DisposeAsync().ConfigureAwait(false);
                    //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetDataStream | Action: Disposed | Guid: {Guid}", _requestData.Guid);
                }
                catch (ObjectDisposedException)
                {
                    //_logger.LogInformation("[BLOB-TRACE] AsyncWorker.ResetDataStream | Action: AlreadyDisposed | Guid: {Guid}", _requestData.Guid);
                    // Stream was already disposed, ignore
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[BLOB-TRACE] AsyncWorker.ResetDataStream | Action: Error | Guid: {Guid} | Error: {ErrorMessage}", 
                        _requestData.Guid, ex.Message);
                }
                finally
                {
                    _requestData.OutputStream = null;
                }
            }
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
            // If it has not already entered startup, abort it and cancel the token     [ ITETCOBO:  ABORTED or active!! ] 
            if (Interlocked.CompareExchange(ref _beginStartup, -1, 0) == 0)
            {
                _cancellationTokenSource?.Cancel();
                _requestData.runAsync = false;

                // Async Worker has not started, Terminate it
                return true; // Worker was not started, so we terminated it
            }

            // Async Worker task has started setting up, wait for it to finish
            _requestData.AsyncTriggered = await _taskCompletionSource.Task.ConfigureAwait(false);

            if (!_requestData.AsyncTriggered)
            {
                await DisposeAsync().ConfigureAwait(false);
                _requestData.runAsync = false;

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


        // cleanup action
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

            await PersistRequestStateAsync();
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