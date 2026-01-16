using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.Events;

namespace SimpleL7Proxy.Backend
{
    public class BackendTokenProvider : IHostedService
    {
        private readonly Dictionary<string, AccessToken> _tokenDict = new();
        private readonly Dictionary<string, DateTimeOffset> _tokenExpiryDict = new();
        private readonly HashSet<string> _audiences = new();
        private readonly Dictionary<string, Task> _refreshTasks = new();
        private static CancellationToken _cancellationToken = CancellationToken.None;
        private readonly BackendOptions _options;
        private readonly ILogger<BackendTokenProvider> _logger;

        public BackendTokenProvider(
            IOptions<BackendOptions> backendOptions,
            ILogger<BackendTokenProvider> logger)
        {
            _options = backendOptions.Value;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
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

            if (!_tokenDict.ContainsKey(audience) || _tokenExpiryDict[audience] < DateTime.UtcNow)
            {
                // Wait for token to be refreshed
                while (!_tokenDict.ContainsKey(audience) || _tokenExpiryDict[audience] < DateTime.UtcNow)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            return _tokenDict[audience].Token ?? "";
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
            var options = new DefaultAzureCredentialOptions();
            if (_options.UseOAuthGov == true)
            {
                options.AuthorityHost = AzureAuthorityHosts.AzureGovernment;
            }
            var credential = new DefaultAzureCredential(options);
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
                            _tokenDict[audience] = token;
                            _tokenExpiryDict[audience] = token.ExpiresOn;
                            _logger.LogInformation($"[TOKEN] Refreshed token for audience: {audience}, expires: {token.ExpiresOn}");
                            new ProxyEvent()
                            {
                                Type = EventType.Authentication,
                                ["Message"] = $"Refreshed OAuth2 token for audience: {audience}, expires: {token.ExpiresOn}",
                                ["Audience"] = audience,
                                ["ExpiresOn"] = token.ExpiresOn.ToString()
                            }.SendEvent();

                            var delay = Math.Max(0, (token.ExpiresOn - DateTime.UtcNow).TotalMilliseconds - 100);
                            await Task.Delay((int)delay, _cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"[TOKEN] Error refreshing token for audience {audience}: {ex.Message}");
                            await Task.Delay(10000, _cancellationToken); // Wait 10s before retry
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
