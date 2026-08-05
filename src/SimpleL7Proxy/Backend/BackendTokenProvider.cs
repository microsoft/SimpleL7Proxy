using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Backend
{
    public class BackendTokenProvider : IHostedService, IReadinessParticipant
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
        private readonly ILogger<BackendTokenProvider> _logger;

        public BackendTokenProvider(
            DefaultCredential defaultCredential,
            ReadinessRegistry readiness,
            ILogger<BackendTokenProvider> logger)
        {
            _defaultCredential = defaultCredential;
            Readiness = readiness;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            // No audiences = no tokens needed; readiness is satisfied immediately.
            // Otherwise the refresh tasks (already running) will mark ready on first success.
            if (_audiences.Count == 0) this.RegisterReady();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

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

        public void StartTokenRefresh()
        {
            foreach (var audience in _audiences)
            {
                StartAudienceRefreshTask(audience);
            }
        }

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
