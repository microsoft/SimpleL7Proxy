using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        public ReadinessParticipantEnum Participant => ReadinessParticipantEnum.BackendTokens;
        public ReadinessRegistry Readiness { get; }
        private readonly Dictionary<string, AccessToken> _tokenDict = new();
        private readonly Dictionary<string, DateTimeOffset> _tokenExpiryDict = new();
        private readonly HashSet<string> _audiences = new();
        private readonly Dictionary<string, Task> _refreshTasks = new();
        private static CancellationToken _cancellationToken = CancellationToken.None;
        private readonly DefaultCredential _defaultCredential;
        private readonly ILogger<BackendTokenProvider> _logger;

        // GCP WIF token state
        private record GcpConfig(string PoolAudience, string ServiceAccount, string AzureClientId);
        private readonly Dictionary<string, string> _gcpTokenDict = new();
        private readonly Dictionary<string, DateTimeOffset> _gcpTokenExpiryDict = new();
        private readonly Dictionary<string, GcpConfig> _gcpConfigs = new();
        private readonly Dictionary<string, Task> _gcpRefreshTasks = new();
        private readonly HttpClient _httpClient = new();

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
            // No audiences and no GCP configs = no tokens needed; satisfy readiness immediately.
            // Otherwise the refresh tasks (already started) will call RegisterReady() on first success.
            if (_audiences.Count == 0 && _gcpConfigs.Count == 0) this.RegisterReady();
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

        public void AddGcpConfig(string poolAudience, string serviceAccount, string azureClientId)
        {
            if (string.IsNullOrEmpty(poolAudience)) return;
            if (_gcpConfigs.ContainsKey(poolAudience)) return;

            var cfg = new GcpConfig(poolAudience, serviceAccount, azureClientId);
            _gcpConfigs[poolAudience] = cfg;
            StartGcpRefreshTask(cfg);
        }

        public async Task<string> GcpToken(string poolAudience)
        {
            if (string.IsNullOrEmpty(poolAudience)) return string.Empty;

            while (!_gcpTokenDict.ContainsKey(poolAudience) || _gcpTokenExpiryDict[poolAudience] < DateTimeOffset.UtcNow)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            return _gcpTokenDict[poolAudience];
        }

        private void StartGcpRefreshTask(GcpConfig cfg)
        {
            if (_gcpRefreshTasks.ContainsKey(cfg.PoolAudience)) return;

            var refreshTask = Task.Run(async () =>
            {
                try
                {
                    while (!_cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Step 1: Get Azure JWT for the configured resource
                            var tokenRequestContext = new TokenRequestContext(new[] { cfg.AzureClientId });
                            var azureToken = await _defaultCredential.Credential.GetTokenAsync(tokenRequestContext, _cancellationToken).ConfigureAwait(false);

                            // Step 2: Exchange Azure JWT for a GCP STS federated token
                            var stsForm = new FormUrlEncodedContent(new[]
                            {
                                new KeyValuePair<string,string>("audience",              cfg.PoolAudience),
                                new KeyValuePair<string,string>("grant_type",            "urn:ietf:params:oauth:grant-type:token-exchange"),
                                new KeyValuePair<string,string>("requested_token_type",  "urn:ietf:params:oauth:token-type:access_token"),
                                new KeyValuePair<string,string>("scope",                 "https://www.googleapis.com/auth/cloud-platform"),
                                new KeyValuePair<string,string>("subject_token_type",    "urn:ietf:params:oauth:token-type:jwt"),
                                new KeyValuePair<string,string>("subject_token",         azureToken.Token),
                            });
                            var stsResponse = await _httpClient.PostAsync("https://sts.googleapis.com/v1/token", stsForm, _cancellationToken).ConfigureAwait(false);
                            stsResponse.EnsureSuccessStatusCode();
                            using var stsDoc = JsonDocument.Parse(await stsResponse.Content.ReadAsStringAsync(_cancellationToken).ConfigureAwait(false));
                            var stsToken = stsDoc.RootElement.GetProperty("access_token").GetString()
                                ?? throw new InvalidOperationException("GCP STS response missing access_token");

                            // Step 3: Impersonate the GCP service account to get a final access token
                            var saUrl = $"https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/{cfg.ServiceAccount}:generateAccessToken";
                            var saRequest = new HttpRequestMessage(HttpMethod.Post, saUrl);
                            saRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", stsToken);
                            saRequest.Content = new StringContent(
                                "{\"scope\":[\"https://www.googleapis.com/auth/cloud-platform\"]}",
                                Encoding.UTF8, "application/json");
                            var saResponse = await _httpClient.SendAsync(saRequest, _cancellationToken).ConfigureAwait(false);
                            saResponse.EnsureSuccessStatusCode();
                            using var saDoc = JsonDocument.Parse(await saResponse.Content.ReadAsStringAsync(_cancellationToken).ConfigureAwait(false));
                            var gcpToken    = saDoc.RootElement.GetProperty("accessToken").GetString()
                                ?? throw new InvalidOperationException("GCP SA response missing accessToken");
                            var expireTime  = DateTimeOffset.Parse(saDoc.RootElement.GetProperty("expireTime").GetString()
                                ?? throw new InvalidOperationException("GCP SA response missing expireTime"));

                            _gcpTokenDict[cfg.PoolAudience]       = gcpToken;
                            _gcpTokenExpiryDict[cfg.PoolAudience] = expireTime;
                            this.RegisterReady(); // idempotent — first successful fetch satisfies the gate

                            _logger.LogInformation("[TOKEN] Refreshed GCP token for pool: {Pool}, SA: {SA}, expires: {Expires}",
                                cfg.PoolAudience, cfg.ServiceAccount, expireTime);
                            new ProxyEvent()
                            {
                                Type = EventType.Authentication,
                                ["Message"]   = $"Refreshed GCP token for SA: {cfg.ServiceAccount}",
                                ["Pool"]      = cfg.PoolAudience,
                                ["ExpiresOn"] = expireTime.ToString()
                            }.SendEvent();

                            // Refresh 5 minutes before expiry
                            var delay = Math.Max(0, (expireTime - DateTimeOffset.UtcNow).TotalMilliseconds - 300_000);
                            await Task.Delay((int)delay, _cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("[TOKEN] Error refreshing GCP token for pool {Pool}: {Msg}", cfg.PoolAudience, ex.Message);
                            await Task.Delay(10_000, _cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[SHUTDOWN] GCP token refresh for pool {Pool} exiting.", cfg.PoolAudience);
                }
                catch (Exception ex)
                {
                    _logger.LogError("[TOKEN] Fatal error in GCP refresh loop for pool {Pool}: {Msg}", cfg.PoolAudience, ex.Message);
                }
            }, _cancellationToken);

            _gcpRefreshTasks[cfg.PoolAudience] = refreshTask;
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
                            _tokenDict[audience] = token;
                            _tokenExpiryDict[audience] = token.ExpiresOn;
                            this.RegisterReady(); // idempotent — first successful fetch satisfies the gate
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
