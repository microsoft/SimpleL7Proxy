using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using SimpleL7Proxy.Auth;
using SimpleL7Proxy.Config;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Exercises the real Azure backend token provider against configured protected endpoints.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AzureTokenLongRunIntegrationTests : IRegressionTestMetadata {
    private static readonly JsonSerializerOptions s_jsonOptions = new() {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private static readonly JsonWebTokenHandler s_tokenHandler = new();

    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature> {
            ["azure-token-long-run"] = new(
                "Authentication",
                "Azure token acquisition over 24 hours",
                "Checks token acquisition, refresh, configured claims, and delivery to protected endpoints over an extended run.")
        };

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Acquires and exercises Azure access tokens for the configured duration, which defaults to 24 hours.
    /// </summary>
    [TestMethod]
    [RegressionTestCase(
        "azure-token-long-run",
        "Azure tokens retain expected claims during a 24-hour run",
        "Repeatedly gets tokens from AzureProvider, validates configured claims and lifetime, calls each protected endpoint, and appends every observation to one JSON Lines file.")]
    [TestCategory("Integration")]
    [TestCategory("Authentication")]
    [TestCategory("Load")]
    [TestCategory("LongRunning")]
    [Timeout(88_200_000)]
    public async Task AzureProvider_MaintainsExpectedClaimsForConfiguredDuration() {
        var (settings, configPath) = await AzureTokenLongRunSettings.LoadAsync();
        if (!settings.Enabled) {
            Assert.Inconclusive(
                "Enable configs/azure-token-long-run.local.json or set AZURE_TOKEN_LONG_RUN_CONFIG_PATH " +
                "to an enabled config before running the longrun suite.");
            return;
        }

        settings.Validate();
        var outputPath = ResolveOutputPath(settings.OutputPath, configPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.AppendAllTextAsync(outputPath, string.Empty, new UTF8Encoding(false));
        TestContext.AddResultFile(outputPath);
        TestContext.WriteLine($"Azure token observations: {outputPath}");

        var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var startedAtUtc = DateTimeOffset.UtcNow;
        var deadlineUtc = startedAtUtc.AddSeconds(settings.DurationSeconds);
        long checks = 0;
        long failures = 0;

        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(options => {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
                options.UseUtcTimestamp = true;
            }));
        using var providerCancellation = new CancellationTokenSource();
        var proxyConfig = new ProxyConfig { UseOAuthGov = settings.UseAzureGovernment };
        var readiness = new ReadinessRegistry(
            Options.Create(proxyConfig),
            loggerFactory.CreateLogger<ReadinessRegistry>());
        var provider = new AzureProvider(
            new DefaultCredential(proxyConfig),
            readiness,
            loggerFactory.CreateLogger<AzureProvider>());
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var pendingTokenRequests = new Dictionary<string, Task<string>>(StringComparer.Ordinal);

        try {
            await provider.StartAsync(providerCancellation.Token);
            foreach (var audience in settings.Endpoints.Select(endpoint => endpoint.Audience).Distinct(StringComparer.Ordinal)) {
                provider.AddAudience(audience);
            }

            long cycle = 0;
            do {
                cycle++;
                var cycleOutcomes = new List<EndpointCheckOutcome>(settings.Endpoints.Count);
                foreach (var endpoint in settings.Endpoints) {
                    var outcome = await CheckEndpointAsync(
                        provider,
                        pendingTokenRequests,
                        client,
                        endpoint,
                        settings,
                        outputPath,
                        runId,
                        cycle);
                    cycleOutcomes.Add(outcome);
                    checks++;
                    if (!outcome.Success) {
                        failures++;
                    }
                }

                if (cycle == 1 && cycleOutcomes.All(outcome => !outcome.TokenAcquired)) {
                    Assert.Fail(
                        $"AzureProvider did not acquire a token for any configured endpoint. See {outputPath}.");
                }

                var remaining = deadlineUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) {
                    break;
                }

                var interval = TimeSpan.FromSeconds(settings.RequestIntervalSeconds);
                await Task.Delay(remaining < interval ? remaining : interval);
            } while (DateTimeOffset.UtcNow < deadlineUtc);
        }
        finally {
            providerCancellation.Cancel();
            await provider.StopAsync(CancellationToken.None);
        }

        Assert.AreEqual(
            0L,
            failures,
            $"{failures} of {checks} Azure token endpoint checks failed. See {outputPath}.");
    }

    private async Task<EndpointCheckOutcome> CheckEndpointAsync(
        AzureProvider provider,
        Dictionary<string, Task<string>> pendingTokenRequests,
        HttpClient client,
        AzureTokenEndpointSettings endpoint,
        AzureTokenLongRunSettings settings,
        string outputPath,
        string runId,
        long cycle) {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var claims = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string? tokenFingerprint = null;
        string? tokenIssuer = null;
        DateTime? tokenValidFromUtc = null;
        DateTime? tokenExpiresOnUtc = null;
        int? statusCode = null;
        string? reasonPhrase = null;
        double? requestDurationMilliseconds = null;
        var tokenAcquired = false;
        var endpointAcceptedToken = false;
        var acquisitionStopwatch = Stopwatch.StartNew();

        string? token = null;
        Task<string>? tokenRequest = null;
        try {
            if (!pendingTokenRequests.TryGetValue(endpoint.Audience, out tokenRequest) ||
                tokenRequest.IsCompleted) {
                tokenRequest = provider.OAuth2Token(endpoint.Audience);
                pendingTokenRequests[endpoint.Audience] = tokenRequest;
            }

            token = await tokenRequest
                .WaitAsync(TimeSpan.FromSeconds(settings.TokenAcquisitionTimeoutSeconds));
            tokenAcquired = !string.IsNullOrWhiteSpace(token);
            if (!tokenAcquired) {
                errors.Add("AzureProvider returned an empty token.");
            }
        }
        catch (TimeoutException) {
            errors.Add(
                $"Token acquisition exceeded {settings.TokenAcquisitionTimeoutSeconds} seconds.");
        }
        catch (Exception exception) {
            errors.Add($"Token acquisition failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally {
            if (tokenRequest?.IsCompleted == true) {
                pendingTokenRequests.Remove(endpoint.Audience);
            }
        }
        acquisitionStopwatch.Stop();

        if (tokenAcquired && token != null) {
            tokenFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
                .ToLowerInvariant();
            try {
                if (!s_tokenHandler.CanReadToken(token)) {
                    errors.Add("The acquired token is not a readable JWT.");
                }
                else {
                    var jwt = s_tokenHandler.ReadJsonWebToken(token);
                    claims = ReadClaims(jwt);
                    tokenIssuer = jwt.Issuer;
                    tokenValidFromUtc = jwt.ValidFrom == DateTime.MinValue ? null : jwt.ValidFrom.ToUniversalTime();
                    tokenExpiresOnUtc = jwt.ValidTo == DateTime.MinValue ? null : jwt.ValidTo.ToUniversalTime();
                    ValidateLifetime(jwt, observedAtUtc.UtcDateTime, errors);
                    ValidateExpectedClaims(endpoint.ExpectedClaims, claims, errors);
                }
            }
            catch (Exception exception) {
                errors.Add($"Token decoding failed: {exception.GetType().Name}: {exception.Message}");
            }

            var requestStopwatch = Stopwatch.StartNew();
            try {
                using var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                foreach (var header in endpoint.Headers) {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value)) {
                        errors.Add($"Request header '{header.Key}' could not be added.");
                    }
                }

                using var requestCancellation = new CancellationTokenSource(
                    TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token);
                statusCode = (int)response.StatusCode;
                reasonPhrase = response.ReasonPhrase;
                await response.Content.CopyToAsync(Stream.Null, requestCancellation.Token);
                endpointAcceptedToken = endpoint.ExpectedStatusCodes.Contains(statusCode.Value);
                if (!endpointAcceptedToken) {
                    errors.Add(
                        $"Endpoint returned HTTP {statusCode}; expected " +
                        string.Join(", ", endpoint.ExpectedStatusCodes));
                }
            }
            catch (OperationCanceledException) {
                errors.Add($"Endpoint request exceeded {settings.RequestTimeoutSeconds} seconds.");
            }
            catch (Exception exception) {
                errors.Add($"Endpoint request failed: {exception.GetType().Name}: {exception.Message}");
            }
            requestStopwatch.Stop();
            requestDurationMilliseconds = requestStopwatch.Elapsed.TotalMilliseconds;
        }

        var success = errors.Count == 0;
        var record = new {
            schemaVersion = 1,
            runId,
            observedAtUtc,
            cycle,
            endpoint = endpoint.Name,
            endpointUrl = endpoint.Url,
            requestedAudience = endpoint.Audience,
            tokenAcquired,
            tokenFingerprintSha256 = tokenFingerprint,
            tokenIssuer,
            tokenValidFromUtc,
            tokenExpiresOnUtc,
            tokenAcquisitionMilliseconds = acquisitionStopwatch.Elapsed.TotalMilliseconds,
            endpointAcceptedToken,
            statusCode,
            reasonPhrase,
            requestDurationMilliseconds,
            claims,
            success,
            errors
        };
        await AppendJsonLineAsync(outputPath, record);
        TestContext.WriteLine(
            $"Cycle {cycle} {endpoint.Name}: {(success ? "PASS" : "FAIL")} " +
            $"HTTP={statusCode?.ToString() ?? "none"} token={tokenFingerprint?[..12] ?? "none"}");
        return new EndpointCheckOutcome(success, tokenAcquired);
    }

    private static Dictionary<string, string[]> ReadClaims(JsonWebToken token) {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in token.Claims) {
            if (!values.TryGetValue(claim.Type, out var claimValues)) {
                claimValues = [];
                values[claim.Type] = claimValues;
            }

            foreach (var value in ExpandClaimValue(claim.Value)) {
                if (!claimValues.Contains(value, StringComparer.Ordinal)) {
                    claimValues.Add(value);
                }
            }
        }

        return values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ExpandClaimValue(string value) {
        if (!value.StartsWith('[') || !value.EndsWith(']')) {
            return [value];
        }

        try {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Array) {
                return document.RootElement.EnumerateArray()
                    .Select(ToClaimString)
                    .ToArray();
            }
        }
        catch (JsonException) {
        }

        return [value];
    }

    private static void ValidateLifetime(JsonWebToken token, DateTime observedAtUtc, List<string> errors) {
        if (token.ValidTo == DateTime.MinValue) {
            errors.Add("Token does not contain an expiration time.");
        }
        else if (token.ValidTo.ToUniversalTime() <= observedAtUtc) {
            errors.Add($"Token expired at {token.ValidTo.ToUniversalTime():O}.");
        }

        if (token.ValidFrom != DateTime.MinValue && token.ValidFrom.ToUniversalTime() > observedAtUtc) {
            errors.Add($"Token is not valid before {token.ValidFrom.ToUniversalTime():O}.");
        }
    }

    private static void ValidateExpectedClaims(
        IReadOnlyDictionary<string, JsonElement> expectedClaims,
        IReadOnlyDictionary<string, string[]> actualClaims,
        List<string> errors) {
        foreach (var expectedClaim in expectedClaims) {
            if (!actualClaims.TryGetValue(expectedClaim.Key, out var actualValues)) {
                errors.Add($"Required claim '{expectedClaim.Key}' is missing.");
                continue;
            }

            foreach (var expectedValue in ReadExpectedValues(expectedClaim.Key, expectedClaim.Value)) {
                if (!actualValues.Contains(expectedValue, StringComparer.Ordinal)) {
                    errors.Add(
                        $"Claim '{expectedClaim.Key}' does not contain expected value '{expectedValue}'.");
                }
            }
        }
    }

    private static IReadOnlyList<string> ReadExpectedValues(string claimName, JsonElement value) {
        if (value.ValueKind == JsonValueKind.Array) {
            var values = value.EnumerateArray().Select(ToExpectedClaimString).ToArray();
            if (values.Length == 0) {
                throw new InvalidOperationException(
                    $"Expected claim '{claimName}' must contain at least one value.");
            }
            return values;
        }

        return [ToExpectedClaimString(value)];
    }

    private static string ToExpectedClaimString(JsonElement value) {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) {
            return ToClaimString(value);
        }

        throw new InvalidOperationException(
            "Expected claim values must be strings, numbers, booleans, or arrays of those values.");
    }

    private static string ToClaimString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

    private static async Task AppendJsonLineAsync(string path, object record) {
        var line = JsonSerializer.Serialize(record, s_jsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, new UTF8Encoding(false));
    }

    private static string ResolveOutputPath(string? configuredPath, string configPath) {
        if (!string.IsNullOrWhiteSpace(configuredPath)) {
            return Path.GetFullPath(
                configuredPath,
                Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory());
        }

        var executionDirectory = Environment.GetEnvironmentVariable("REGRESSION_EXECUTION_DIR");
        if (!string.IsNullOrWhiteSpace(executionDirectory)) {
            return Path.Combine(Path.GetFullPath(executionDirectory), "azure-token-claims.jsonl");
        }

        return Path.Combine(
            Directory.GetCurrentDirectory(),
            "results",
            "azure-token-claims.jsonl");
    }

    private sealed record EndpointCheckOutcome(bool Success, bool TokenAcquired);

    private sealed class AzureTokenLongRunSettings {
        public bool Enabled { get; init; }
        public int DurationSeconds { get; init; } = 86_400;
        public int RequestIntervalSeconds { get; init; } = 300;
        public int TokenAcquisitionTimeoutSeconds { get; init; } = 180;
        public int RequestTimeoutSeconds { get; init; } = 60;
        public bool UseAzureGovernment { get; init; }
        public string? OutputPath { get; init; }
        public List<AzureTokenEndpointSettings> Endpoints { get; init; } = [];

        public static async Task<(AzureTokenLongRunSettings Settings, string ConfigPath)> LoadAsync() {
            var configuredPath = Environment.GetEnvironmentVariable("AZURE_TOKEN_LONG_RUN_CONFIG_PATH");
            var configDirectory = Path.Combine(AppContext.BaseDirectory, "configs");
            var localPath = Path.Combine(configDirectory, "azure-token-long-run.local.json");
            var configPath = string.IsNullOrWhiteSpace(configuredPath)
                ? File.Exists(localPath)
                    ? localPath
                    : Path.Combine(configDirectory, "azure-token-long-run.json")
                : Path.GetFullPath(configuredPath);
            if (!File.Exists(configPath)) {
                throw new FileNotFoundException("Azure token long-run config was not found.", configPath);
            }

            var settings = JsonSerializer.Deserialize<AzureTokenLongRunSettings>(
                await File.ReadAllTextAsync(configPath),
                s_jsonOptions) ?? throw new InvalidOperationException(
                    "Azure token long-run config is empty.");
            return (settings, configPath);
        }

        public void Validate() {
            ValidateRange(DurationSeconds, 1, 86_400, nameof(DurationSeconds));
            ValidateRange(RequestIntervalSeconds, 1, 3_600, nameof(RequestIntervalSeconds));
            ValidateRange(TokenAcquisitionTimeoutSeconds, 1, 600, nameof(TokenAcquisitionTimeoutSeconds));
            ValidateRange(RequestTimeoutSeconds, 1, 600, nameof(RequestTimeoutSeconds));
            if (Endpoints.Count == 0) {
                throw new InvalidOperationException("At least one token endpoint must be configured.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in Endpoints) {
                endpoint.Validate();
                if (!names.Add(endpoint.Name)) {
                    throw new InvalidOperationException($"Endpoint name '{endpoint.Name}' is duplicated.");
                }
            }
        }

        private static void ValidateRange(int value, int minimum, int maximum, string name) {
            if (value < minimum || value > maximum) {
                throw new InvalidOperationException(
                    $"{name} must be between {minimum} and {maximum} seconds.");
            }
        }
    }

    private sealed class AzureTokenEndpointSettings {
        public required string Name { get; init; }
        public required string Url { get; init; }
        public string Method { get; init; } = "GET";
        public required string Audience { get; init; }
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<int> ExpectedStatusCodes { get; init; } = [200];
        public Dictionary<string, JsonElement> ExpectedClaims { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public void Validate() {
            if (string.IsNullOrWhiteSpace(Name)) {
                throw new InvalidOperationException("Every endpoint must have a name.");
            }
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var endpointUri) ||
                endpointUri.Scheme != Uri.UriSchemeHttps) {
                throw new InvalidOperationException($"Endpoint '{Name}' URL must be an absolute HTTPS URL.");
            }
            if (string.IsNullOrWhiteSpace(Audience)) {
                throw new InvalidOperationException($"Endpoint '{Name}' must have an audience.");
            }
            if (string.IsNullOrWhiteSpace(Method)) {
                throw new InvalidOperationException($"Endpoint '{Name}' must have an HTTP method.");
            }
            _ = new HttpMethod(Method);
            if (Headers.Keys.Any(header =>
                string.Equals(header, "Authorization", StringComparison.OrdinalIgnoreCase))) {
                throw new InvalidOperationException(
                    $"Endpoint '{Name}' cannot configure the Authorization header.");
            }
            if (ExpectedStatusCodes.Count == 0 ||
                ExpectedStatusCodes.Any(status => status is < 100 or > 599)) {
                throw new InvalidOperationException(
                    $"Endpoint '{Name}' expectedStatusCodes must contain valid HTTP status codes.");
            }
            if (ExpectedClaims.Count == 0) {
                throw new InvalidOperationException(
                    $"Endpoint '{Name}' must configure at least one expected claim.");
            }
            foreach (var expectedClaim in ExpectedClaims) {
                _ = ReadExpectedValues(expectedClaim.Key, expectedClaim.Value);
            }
        }
    }
}