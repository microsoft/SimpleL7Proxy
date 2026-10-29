using System.Runtime.CompilerServices;
using System.Net.Http.Headers;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Microsoft.AspNetCore.WebUtilities;

namespace CompanionApp.Components.Shared;

/// <summary>
/// Provides Container Apps management and console-log calls without owning UI or background tasks.
/// </summary>
public sealed class ContainerAppManagementService {
    private readonly ArmClient _armClient;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes management calls and a dedicated streaming HTTP client factory.</summary>
    public ContainerAppManagementService(ArmClient armClient, IHttpClientFactory httpClientFactory) {
        ArgumentNullException.ThrowIfNull(armClient);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _armClient = armClient;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Lists apps in the specified resource group, fetching pages on demand.</summary>
    public AsyncPageable<ContainerAppResource> ListAppsAsync(
        ResourceIdentifier resourceGroupId, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(resourceGroupId);
        if (resourceGroupId.ResourceType != "Microsoft.Resources/resourceGroups") {
            throw new ArgumentException("A resource-group resource ID is required.", nameof(resourceGroupId));
        }

        return _armClient.GetResourceGroupResource(resourceGroupId)
            .GetContainerApps().GetAllAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Reads the app's current configuration and provisioning status.</summary>
    public async Task<ContainerAppData> GetAppAsync(
        ResourceIdentifier appId, CancellationToken cancellationToken = default) {
        var response = await GetAppResource(appId).GetAsync(cancellationToken);
        return response.Value.Data;
    }

    /// <summary>Lists revisions, including their activation, health, and running states.</summary>
    public AsyncPageable<ContainerAppRevisionResource> ListRevisionsAsync(
        ResourceIdentifier appId, CancellationToken cancellationToken = default) {
        return GetAppResource(appId).GetContainerAppRevisions()
            .GetAllAsync(cancellationToken: cancellationToken);
    }

    /// <summary>Lists replicas and their container readiness, restart counts, and log endpoints.</summary>
    public AsyncPageable<ContainerAppReplicaResource> ListReplicasAsync(
        ResourceIdentifier appId, string revisionName, CancellationToken cancellationToken = default) {
        return GetRevisionResource(appId, revisionName).GetContainerAppReplicas()
            .GetAllAsync(cancellationToken);
    }

    /// <summary>
    /// Polls a revision and its replicas immediately, then after each interval until cancelled.
    /// Snapshots are sequential ARM reads, not atomic. Errors propagate; no reconnect is attempted.
    /// </summary>
    public async IAsyncEnumerable<(ContainerAppRevisionData Revision, IReadOnlyList<ContainerAppReplicaData> Replicas)>
        WatchRevisionAsync(ResourceIdentifier appId, string revisionName, TimeSpan pollInterval,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        if (pollInterval <= TimeSpan.Zero || pollInterval.TotalMilliseconds > uint.MaxValue - 1) {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        var revision = GetRevisionResource(appId, revisionName);
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await revision.GetAsync(cancellationToken);
            var replicas = new List<ContainerAppReplicaData>();
            await foreach (var replica in revision.GetContainerAppReplicas().GetAllAsync(cancellationToken)) {
                replicas.Add(replica.Data);
            }

            yield return (response.Value.Data, replicas.AsReadOnly());
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Requests a restart of all replicas in the explicitly named revision.
    /// Completion acknowledges the management call, not application readiness; poll separately.
    /// </summary>
    public async Task RestartRevisionAsync(
        ResourceIdentifier appId, string revisionName, CancellationToken cancellationToken = default) {
        await GetRevisionResource(appId, revisionName).RestartRevisionAsync(cancellationToken);
    }

    /// <summary>
    /// Reads JSON console-log records for one replica container, optionally following new records.
    /// Tail lines must be 0 through 300. Cancellation or enumeration disposal closes the connection.
    /// EOF ends enumeration; callers must explicitly reconnect after expiry or replica replacement.
    /// </summary>
    public async IAsyncEnumerable<string> ReadConsoleLogsAsync(
        ResourceIdentifier appId, string revisionName, string replicaName, string containerName,
        bool follow = true, int tailLines = 50,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (tailLines < 0 || tailLines > 300) {
            throw new ArgumentOutOfRangeException(nameof(tailLines), "Tail lines must be between 0 and 300.");
        }

        var app = GetAppResource(appId);
        var revision = GetRevisionResource(appId, revisionName);
        var replica = await revision.GetContainerAppReplicaAsync(replicaName, cancellationToken);
        var container = replica.Value.Data.Containers.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, containerName, StringComparison.Ordinal));
        if (container is null) {
            throw new InvalidOperationException("The specified container was not found in the replica.");
        }

        if (!Uri.TryCreate(container.LogStreamEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Fragment)) {
            throw new InvalidOperationException("The container has no valid HTTPS console-log endpoint.");
        }

        var auth = await app.GetAuthTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(auth.Value.Token)) {
            throw new InvalidOperationException("Container Apps returned an empty log authorization token.");
        }

        var requestUri = QueryHelpers.AddQueryString(endpoint.AbsoluteUri, new Dictionary<string, string?> {
            ["follow"] = follow ? "true" : "false",
            ["output"] = "json",
            ["tailLines"] = tailLines.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Value.Token);
        using var client = _httpClientFactory.CreateClient(nameof(ContainerAppManagementService));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line) {
            if (!string.IsNullOrWhiteSpace(line)) {
                yield return line;
            }
        }
    }

    private ContainerAppResource GetAppResource(ResourceIdentifier appId) {
        ArgumentNullException.ThrowIfNull(appId);
        if (appId.ResourceType != ContainerAppResource.ResourceType) {
            throw new ArgumentException("A Microsoft.App/containerApps resource ID is required.", nameof(appId));
        }

        return _armClient.GetContainerAppResource(appId);
    }

    private ContainerAppRevisionResource GetRevisionResource(ResourceIdentifier appId, string revisionName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionName);
        GetAppResource(appId);
        return _armClient.GetContainerAppRevisionResource(ContainerAppRevisionResource.CreateResourceIdentifier(
            appId.SubscriptionId, appId.ResourceGroupName, appId.Name, revisionName));
    }
}