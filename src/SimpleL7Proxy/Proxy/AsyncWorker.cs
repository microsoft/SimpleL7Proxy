using System;
using System.Text;
using System.Text.Json;
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
        private int _beginStartup =0; // 0 = not started, 1 = started, -1 = abort startup
        TaskCompletionSource<bool> _taskCompletionSource = new TaskCompletionSource<bool>();
        //private int _completed = 0; // 0 = not completed, 1 = completed
        private RequestData _requestData { get; set; }
        private static  BlobWriter? _blobWriter;
        private static  ILogger<AsyncWorker>? _logger;
        public string ErrorMessage { get; set; } = "";


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
                // wait state... can be cancelled by Terminate
                await Task.Delay(_requestData.Timeout, _cancellationTokenSource.Token).ConfigureAwait(false);


                // Atomically set to running (1) only if not started (0)
                if (Interlocked.CompareExchange(ref _beginStartup, 1, 0) == 0)
                {
                    try
                    {
                        var os = await _blobWriter!.CreateBlobAndGetOutputStreamAsync(_requestData.Guid.ToString()).ConfigureAwait(false);
                        _requestData.OutputStream = new BufferedStream(os);

                        // _requestData.OutputStream.WriteLine($"Request GUID: {_requestData.Guid}");
                        // _requestData.OutputStream.WriteLine($"Request MID: {_requestData.MID}");
                        // _requestData.OutputStream.WriteLine($"Request URL: {_requestData.FullURL}");
                        // _requestData.OutputStream.WriteLine($"Request Timestamp: {_requestData.Timestamp}");
                        // _requestData.OutputStream.WriteLine($"Request Headers: {_requestData.Headers}");
                        // _requestData.OutputStream.WriteLine($"Request Body: {Encoding.UTF8.GetString(_requestData.BodyBytes!)}");

                    }
                    catch (Exception blobEx)
                    {
                        _logger!.LogError($"Failed to create blob: {blobEx.Message}");
                        _taskCompletionSource.TrySetResult (false);
                        ErrorMessage = "Failed to create blob: " + blobEx.Message;
                        return;
                        //proxyEventData["x-Status"] = "Blob Error";
                        // Client disconnected?
                    }

                    RequestResponse Statusmessage = new()
                    {
                        Status = 202,
                        Message = "Request will process async.",
                        RequestGuid = _requestData.Guid.ToString()
                    };

                    try
                    {
                        var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Statusmessage));

                        _requestData.Context!.Response.StatusCode = 202;
                        await _requestData.Context.Response.OutputStream.WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                    }
                    catch (Exception writeEx)
                    {
                        _logger!.LogError($"Failed to write error message: {writeEx.Message}");
                        //proxyEventData["x-Status"] = "Network Error";
                        // Client disconnected?
                    }

                    _logger.LogInformation($"Async: Request MID: {_requestData.MID} Guid: {_requestData.Guid.ToString()} created.");
                    _taskCompletionSource.TrySetResult (true); // Set the task completion source to indicate that the worker has started
                }


            }
            catch (ObjectDisposedException) {
                _taskCompletionSource.TrySetResult (false); // Set the task completion source to indicate that the worker was cancelled
            }
            catch (TaskCanceledException)
            {
                // Timer was cancelled by TryTerminate, do nothing
                _taskCompletionSource.TrySetResult (false); // Set the task completion source to indicate that the worker was cancelled
            }
            finally {
                // Dispose of the cancellation token source
                _cancellationTokenSource.Dispose();
             }

        }

        /// <summary>
        /// We want to terminate this worker if it has not already started up.  If it started, wait for it to finish.
        /// </summary>
        /// <returns><c>true</c> if everything looks good; otherwise, <c>false</c>.</returns>
        public async Task<bool> Synchronize()
        {
            Console.WriteLine($"AsyncWorker: Synchronize called.  Request GUID: {_requestData.Guid}");
            // If it has not already entered startup, abort it and cancel the token
            if (Interlocked.CompareExchange(ref _beginStartup, -1, 0) == 0)
            {
                _cancellationTokenSource?.Cancel();

                Console.WriteLine($"AsyncWorker: Terminating worker.  Request GUID: {_requestData.Guid}");
                return true; // Worker was not started, so we terminated it
            }

            Console.WriteLine($"AsyncWorker: Worker already started. Request GUID: {_requestData.Guid}");

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