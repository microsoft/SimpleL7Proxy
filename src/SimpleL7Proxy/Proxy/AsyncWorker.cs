using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SimpleL7Proxy;
using SimpleL7Proxy.BlobStorage;
using Microsoft.Extensions.Logging;

namespace SimpleL7Proxy.Proxy
{
    /// <summary>
    /// Represents an asynchronous worker that performs a task and disappears after completion.
    /// </summary>
    public class AsyncWorker
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private int _beginStartup = 0; // 0 = not started, 1 = started, -1 = abort startup
        TaskCompletionSource<bool> _taskCompletionSource = new TaskCompletionSource<bool>();
        //private int _completed = 0; // 0 = not completed, 1 = completed
        private RequestData _requestData { get; set; }
        private string _headerBlobUri { get; set; } = "";
        private string _dataBlobUri { get; set; } = "";
        private Stream _hos { get; set; } = null!;
        private static BlobWriter? _blobWriter;
        private static ILogger<AsyncWorker>? _logger;
        public string ErrorMessage { get; set; } = "";

        private static JsonSerializerOptions serialize_options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };


        // Static constructor to initialize the BlobWriter and Logger
        public static void Initialize(BlobWriter blobWriter, ILogger<AsyncWorker> logger)
        {
            _blobWriter = blobWriter;
            _logger = logger;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncWorker"/> class.
        /// </summary>
        /// <param name="data">The request data.</param>
        /// <param name="blobWriter">The blob writer.</param>
        public AsyncWorker(RequestData data)
        {
            _requestData = data;
            if ( !data.runAsync)
            {
                throw new ArgumentException("AsyncWorker can only be used for async requests.");
            }
            _cancellationTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Starts the worker if it has not already been started.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task StartAsync()
        {
            try
            {
                string dataBlobName = "";
                string headerBlobName = "";
                // wait state... can be cancelled by Terminate
                await Task.Delay(_requestData.Timeout, _cancellationTokenSource.Token).ConfigureAwait(false);


                // Atomically set to running (1) only if not started (0)
                if (Interlocked.CompareExchange(ref _beginStartup, 1, 0) == 0)
                {
                    try
                    {
                        dataBlobName = _requestData.Guid.ToString();
                        headerBlobName = dataBlobName + "-Headers";
                        var os = await _blobWriter!.CreateBlobAndGetOutputStreamAsync(dataBlobName).ConfigureAwait(false);
                        _requestData.OutputStream = new BufferedStream(os);
                        _hos = new BufferedStream(await _blobWriter!.CreateBlobAndGetOutputStreamAsync(headerBlobName).ConfigureAwait(false));

                    }
                    catch (Exception blobEx)
                    {
                        _logger!.LogError($"Failed to create blob: {blobEx.Message}");
                        _taskCompletionSource.TrySetResult(false);
                        ErrorMessage = "Failed to create blob: " + blobEx.Message;

                        return;
                    }
                    // create a SAS token for the blob
                    try
                    {
                        _dataBlobUri = _blobWriter!.GenerateSasToken(dataBlobName, TimeSpan.FromMinutes(5));
                        _headerBlobUri = _blobWriter!.GenerateSasToken(headerBlobName, TimeSpan.FromMinutes(5));
                        _requestData.Context!.Response.Headers.Add("x-Data-Blob-SAS-URI", _dataBlobUri);
                        _requestData.Context!.Response.Headers.Add("x-Header-Blob-SAS-URI", _headerBlobUri);
                    }
                    catch (Exception sasEx)
                    {
                        _logger!.LogError($"Failed to create SAS token: {sasEx.Message}");
                        _taskCompletionSource.TrySetResult(false);
                        ErrorMessage = "Failed to create SAS token: " + sasEx.Message;

                        return;
                    }

                    AsyncMessage Statusmessage = new()
                    {
                        Status = 202,
                        Message = "Your request has been accepted for async processing.  You can view the status on the event hub. The final result will be available at the BlobUri.",
                        MID = _requestData.MID,
                        UserId = _requestData.UserID,
                        Guid = _requestData.Guid.ToString(),
                        DataBlobUri = _dataBlobUri,
                        HeaderBlobUri = _headerBlobUri,
                        Timestamp = DateTime.UtcNow
                    };

                    try
                    {
                        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Statusmessage, serialize_options) + "\n");

                        _requestData.Context!.Response.StatusCode = 202;
                        await _requestData.Context.Response.OutputStream.WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                        await _requestData.Context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                        _requestData.Context.Response.Close();
                    }
                    catch (Exception writeEx)
                    {
                        _logger!.LogError($"Failed to write error message: {writeEx.Message}");
                        //proxyEventData["x-Status"] = "Network Error";
                        // Client disconnected?
                    }

                    _logger!.LogInformation($"Async: Request MID: {_requestData.MID} Guid: {_requestData.Guid.ToString()} created.");
                    _taskCompletionSource.TrySetResult(true); // Set the task completion source to indicate that the worker has started
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

        public async Task<bool> WriteHeaders(HttpStatusCode status, WebHeaderCollection headers)
        {
            try
            {
                var header_message = new AsyncHeaders()
                {
                    Status = status.ToString(),
                    Headers = headers,
                    UserId = _requestData.UserID,
                    MID = _requestData.MID,
                    Guid = _requestData.Guid.ToString(),
                    Timestamp = DateTime.UtcNow,
                    BlobUri = _dataBlobUri
                };

                var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header_message, serialize_options) + "\n");
                var bos = new BufferedStream(_hos);
                await bos.WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                await bos.FlushAsync().ConfigureAwait(false);
                bos.Close();
                return true;
            }
            catch (Exception ex)
            {
                _logger!.LogError($"Failed to write headers: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// We want to terminate this worker if it has not already started up.  If it started, wait for it to finish.
        /// </summary>
        /// <returns><c>true</c> if everything looks good; otherwise, <c>false</c>.</returns>
        public async Task<bool> Synchronize()
        {
            // If it has not already entered startup, abort it and cancel the token
            if (Interlocked.CompareExchange(ref _beginStartup, -1, 0) == 0)
            {
                _cancellationTokenSource?.Cancel();

                // Async Worker has not started, Terminate it
                return true; // Worker was not started, so we terminated it
            }

            _requestData.AsyncTriggered = true;
            // Asynnc Worker task already started. Wait for it to finish
            return await _taskCompletionSource.Task.ConfigureAwait(false); // Wait for the worker to start up
        }

        /// <summary>
        /// Checks if the worker has been started.
        /// </summary>
        /// <returns><c>true</c> if the worker has been started; otherwise, <c>false</c>.</returns>
        public bool IsStarted()
        {
            return _beginStartup == 1;
        }
    }
}