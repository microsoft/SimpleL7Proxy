using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using SimpleL7Proxy.Backend;
using Shared.RequestAPI.Models;
using System.Security.Policy;


namespace SimpleL7Proxy.BackupAPI
{
    public class BackupAPIService : IHostedService, IBackupAPIService
    {
        private readonly BackendOptions _options;
        private readonly ILogger<BackupAPIService> _logger;
        public static readonly ConcurrentQueue<RequestAPIDocument> _statusQueue = new();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private bool isShuttingDown = false;
        private Task? writerTask;
        CancellationTokenSource? _cancellationTokenSource;

        // Batch tuning
        private const int MaxDrainPerCycle = 50; // max messages to drain from queue per cycle
        private static readonly TimeSpan FlushIntervalMs = TimeSpan.FromMilliseconds(1000);    // small delay to coalesce bursts (when not shutting down)

        public BackupAPIService(IOptions<BackendOptions> options, ILogger<BackupAPIService> logger)
        {
            _options = options.Value;
            _logger = logger;

            _logger.LogInformation("Backup API feeder configured:");
        }


        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.AsyncModeEnabled)
            {
                _logger.LogCritical("Backup API service starting...");
                _cancellationTokenSource = new CancellationTokenSource();
                _cancellationTokenSource.Token.Register(() =>
                {
                    _logger.LogCritical("Backup API service stopping.");
                });

                // Start the writer task but DON'T await it
                writerTask = Task.Run(() => EventWriter(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

                // Return immediately - let the writer task run in the background
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            isShuttingDown = true;
            return Task.CompletedTask;
        }

        public bool UpdateStatus(RequestAPIDocument message)
        {
            try
            {
                _logger.LogDebug($"Enqueuing status message for UserId: {message.userID}, Status: {message.status}");
                _statusQueue.Enqueue(message);
                _queueSignal.Release();

                return true; // Enqueue succeeded
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue message to the status queue.");
                return false; // Enqueue failed
            }
        }

        public async Task EventWriter(CancellationToken token)
        {

            _logger.LogCritical("Starting Backup API service...");

            try
            {
                await Task.Run(() => FeederTask(token), token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                // Task was canceled, exit gracefully
                _logger.LogInformation("Backup API service task was canceled.");
            }
            catch (OperationCanceledException)
            {
                // Operation was canceled, exit gracefully
                _logger.LogInformation($"Backup API service shutdown initiated: {_statusQueue.Count()} items need to be flushed.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while calling Backup API.: " + ex);
            }
            finally
            {
                // Flush all items in batches : should never be the case 
                var cts = new CancellationTokenSource().Token;

                var drained = new List<RequestAPIDocument>();
                while (_statusQueue.TryDequeue(out var statusMessage))
                {
                    drained.Add(statusMessage);
                }

                if (drained.Count > 0)
                {
                    try
                    {
                        await SendBatch(drained, cts).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while flushing backup API service. Continuing.");
                    }
                }
            }

            _logger.LogInformation("Backup API service is stopping.");
        }

        DateTime _lastDrainTime = DateTime.UtcNow;

        private async Task FeederTask(CancellationToken token)
        {
            var drained = new List<RequestAPIDocument>(MaxDrainPerCycle);
            while (!isShuttingDown || !_statusQueue.IsEmpty)
            {

                // don't repeat this loop more than once every FlushIntervalMs unless we are shutting down
                var delta = DateTime.UtcNow - _lastDrainTime;
                if (delta < FlushIntervalMs && !isShuttingDown && !token.IsCancellationRequested)
                {
                    delta = FlushIntervalMs - delta;
                    _logger.LogError($"Waiting for coalescing delay of {delta.TotalMilliseconds} ms before next drain cycle.");
                    await Task.Delay(delta, token).ConfigureAwait(false);
                }
                _lastDrainTime = DateTime.UtcNow;

                // Drain all available items before waiting
                while (_statusQueue.TryDequeue(out var item))
                {
                    // Process item (e.g., add to batch, send, etc.)
                    drained.Add(item);
                    if (drained.Count >= MaxDrainPerCycle)
                    {
                        await SendBatch(drained, token).ConfigureAwait(false);
                        drained.Clear();
                    }
                }

                // If any remain after draining, process them
                if (drained.Count > 0)
                {
                    await SendBatch(drained, token).ConfigureAwait(false);
                    drained.Clear();
                }

                // Now wait for a signal before next round
                while (_statusQueue.IsEmpty && !token.IsCancellationRequested)
                {
                    await _queueSignal.WaitAsync(token).ConfigureAwait(false);
                }
                _logger.LogDebug($"Loop: cancelRequest: {token.IsCancellationRequested}, queueCount: {_statusQueue.Count}");
            }
        }

        private static AccessToken _accessToken;
        private int _processingState = 0; // 0 = not waiting, 1 = waiting for signal

        private static async Task<AccessToken> GetAccessToken(string url)
        {
            if (_accessToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return _accessToken;
            }

            var credential = new DefaultAzureCredential();
            _accessToken = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { url }));

            return _accessToken;
        }

        string url = "http://localhost:7071";
        static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new CaseInsensitiveEnumConverter<RequestAPIStatusEnum>() }
        };

        private bool needsToken = false;

        private async Task SendBatch(List<RequestAPIDocument> items, CancellationToken cancelToken)
        {
            using var httpClient = new HttpClient();

            if (needsToken)
            {
                var token = await GetAccessToken(url);
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }

            foreach (var item in items)
            {

                string jsonContent;
                string uri;

                if (item.status == RequestAPIStatusEnum.New)
                {
                    jsonContent = JsonSerializer.Serialize(item, jsonOptions);
                    uri = $"{url}/api/new/{item.id}";
                }
                else
                {
                    // Handle other statuses
                    jsonContent = $"{{\"status\":\"{item.status}\"}}";
                    uri = $"{url}/api/update/{item.id}";
                }

                var httpContent = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
                try
                {
                    var response = await httpClient.PostAsync(uri, httpContent, cancelToken);
                    response.EnsureSuccessStatusCode();
                    var content = await response.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    // there was a problem sending,  requeue the message for retry later
                    _logger.LogError(ex, $"Error sending message to Backup API for UserId: {item.userID}, Status: {item.status}. Requeuing message.");
                    _statusQueue.Enqueue(item);
                }
            }
        }
    }
}