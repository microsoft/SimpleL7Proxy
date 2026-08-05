using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Auth
{
    public class AzureProvider : IBackendTokenProvider, IHostedService, IReadinessParticipant
    {
        private static readonly TimeSpan _tokenExpiryBuffer = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan _tokenRefreshExpiryBuffer = TimeSpan.FromMilliseconds(200);
        public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.BackendTokens;
        public ReadinessRegistry Readiness { get; }
        private readonly ConcurrentDictionary<string, AccessToken> _tokenDict = new();
        private readonly HashSet<string> _audiences = new();
        private readonly Dictionary<string, Task> _refreshTasks = new();
        private static CancellationToken _cancellationToken = CancellationToken.None;
        private readonly DefaultCredential _defaultCredential;
        private readonly ILogger<AzureProvider> _logger;

        /// <summary>
        /// Initializes a token provider with the credential, readiness registry, and logger used for backend authentication.
        /// </summary>
        /// <param name="defaultCredential">The credential used to request backend access tokens.</param>
        /// <param name="readiness">The registry that tracks token-provider readiness.</param>
        /// <param name="logger">The logger used for token refresh diagnostics.</param>
        public AzureProvider(
            DefaultCredential defaultCredential,
            ReadinessRegistry readiness,
            ILogger<AzureProvider> logger)
        {
            _defaultCredential = defaultCredential;
            Readiness = readiness;
            _logger = logger;
        }

        /// <summary>
        /// Starts the token provider and immediately marks it ready when no token audiences are registered.
        /// </summary>
        /// <param name="cancellationToken">A token that indicates startup cancellation.</param>
        /// <returns>A completed task.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            // No audiences = no tokens needed; readiness is satisfied immediately.
            // Otherwise the refresh tasks (already running) will mark ready on first success.
            if (_audiences.Count == 0) this.RegisterReady();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Completes the hosted service stop operation.
        /// </summary>
        /// <param name="cancellationToken">A token that indicates the stop operation should no longer be graceful.</param>
        /// <returns>A completed task.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Registers a non-empty token audience and starts its refresh task when it is first added.
        /// </summary>
        /// <param name="audience">The OAuth 2.0 audience to register.</param>
        public void AddAudience(string audience)
        {
            if (!string.IsNullOrEmpty(audience))
            {
                if (_audiences.Add(audience))
                {
                    StartAudienceRefreshTask(audience);
                }
            }
        }

        /// <summary>
        /// Returns a valid OAuth 2.0 token for an audience, waiting for the refresh task when necessary.
        /// </summary>
        /// <param name="audience">The OAuth 2.0 audience whose token is required.</param>
        /// <returns>The access token, or an empty string when no audience is provided.</returns>
        public async Task<string> OAuth2Token(string? audience = null)
        {
            if (string.IsNullOrEmpty(audience)) return string.Empty;

            while (true)
            {
                if (_tokenDict.TryGetValue(audience, out var token)
                    && token.ExpiresOn > DateTimeOffset.UtcNow.Add(_tokenExpiryBuffer)
                    && !string.IsNullOrWhiteSpace(token.Token))
                {
                    return token.Token;
                }

                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Ensures that a token refresh task is running for every registered audience.
        /// </summary>
        public void StartTokenRefresh()
        {
            foreach (var audience in _audiences)
            {
                StartAudienceRefreshTask(audience);
            }
        }

        /// <summary>
        /// Starts the background token refresh loop for an audience unless one is already registered.
        /// </summary>
        /// <param name="audience">The OAuth 2.0 audience whose token is refreshed.</param>
        private void StartAudienceRefreshTask(string audience)
        {
            if (_refreshTasks.ContainsKey(audience)) return;
            var credential = _defaultCredential.Credential;
            var refreshTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            var tokenRequestContext = new TokenRequestContext(new[] { audience });
                            var token = await credential.GetTokenAsync(tokenRequestContext, _cancellationToken);
                            if (string.IsNullOrWhiteSpace(token.Token))
                            {
                                new ProxyEvent()
                                {
                                    Type = EventType.Exception,
                                    ["Error"] = "EmptyToken",
                                    ["Message"] = $"OAuth2 token refresh returned an empty token for audience: {audience}, expires: {token.ExpiresOn}",
                                    ["Audience"] = audience,
                                    ["ExpiresOn"] = token.ExpiresOn.ToString()
                                }.SendEvent();

                                await Task.Delay(500, _cancellationToken);
                                continue;
                            }

                            _tokenDict[audience] = token;
                            this.RegisterReady(); // idempotent — first successful fetch satisfies the gate
                            _logger.LogInformation($"[TOKEN] Refreshed token for audience: {audience}, expires: {token.ExpiresOn}");
                            new ProxyEvent()
                            {
                                Type = EventType.Authentication,
                                ["Message"] = $"Refreshed OAuth2 token for audience: {audience}, expires: {token.ExpiresOn}",
                                ["Audience"] = audience,
                                ["ExpiresOn"] = token.ExpiresOn.ToString()
                            }.SendEvent();

                            var delay = Math.Max(0, (token.ExpiresOn - DateTimeOffset.UtcNow - _tokenRefreshExpiryBuffer).TotalMilliseconds);
                            await Task.Delay((int)delay, _cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[TOKEN] Error refreshing token for audience {audience}: {ex.Message}");
                            await Task.Delay(5000, _cancellationToken); // Wait 5s before retry
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"[SHUTDOWN] Token refresh operation for audience {audience} exiting.");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[TOKEN] Error in token refresh loop for audience {audience}: {ex.Message}");
                }
            }, _cancellationToken);
            _refreshTasks[audience] = refreshTask;
        }
    }
}
