using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Containers.ContainerRegistry;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace CompanionApp.Components.Shared;

/// <summary>
/// Copies container images from the public release registry into the deployment's registry.
/// The public registry is read anonymously; the deployment registry is written with the app's
/// <see cref="DefaultAzureCredential"/>, which requires AcrPush on that registry.
/// </summary>
public sealed class ImageSyncService
{
    /// <summary>Login server of the public release registry.</summary>
    public const string SourceLoginServer = "publicnvmacr.azurecr.io";

    private static readonly string[] IndexMediaTypes =
    {
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json"
    };

    private static readonly Regex RepositoryNamePattern = new("^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex TagPattern = new("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

    private readonly DefaultAzureCredential _credential;
    private readonly ILogger<ImageSyncService> _logger;
    private readonly ContainerRegistryClient _sourceClient;

    public ImageSyncService(DefaultAzureCredential credential, IOptions<CompanionAppOptions> options, ILogger<ImageSyncService> logger)
    {
        _credential = credential;
        _logger = logger;
        _sourceClient = new ContainerRegistryClient(SourceEndpoint);
        TargetLoginServer = ToLoginServer(options.Value.ProxyAcr);
    }

    /// <summary>Endpoint of the public release registry.</summary>
    public static Uri SourceEndpoint { get; } = new($"https://{SourceLoginServer}");

    /// <summary>Login server of the deployment registry, or empty when <c>CompanionApp__proxyacr</c> is not set.</summary>
    public string TargetLoginServer { get; }

    /// <summary>True when a deployment registry is configured.</summary>
    public bool HasTarget => TargetLoginServer.Length > 0;

    /// <summary>Lists the repositories published in the public release registry.</summary>
    public async Task<IReadOnlyList<string>> GetSourceRepositoriesAsync(CancellationToken cancellationToken)
    {
        var repositories = new List<string>();
        await foreach (var name in _sourceClient.GetRepositoryNamesAsync(cancellationToken))
        {
            repositories.Add(name);
        }
        repositories.Sort(StringComparer.Ordinal);
        return repositories;
    }

    /// <summary>Lists the tags of a repository in the public release registry.</summary>
    public Task<IReadOnlyList<string>> GetSourceTagsAsync(string repository, CancellationToken cancellationToken) =>
        GetTagsAsync(new ContainerRegistryContentClient(SourceEndpoint, repository).Pipeline, SourceEndpoint, repository, cancellationToken);

    /// <summary>Lists the tags of a repository in the deployment registry; empty when the repository does not exist.</summary>
    public Task<IReadOnlyList<string>> GetTargetTagsAsync(string repository, CancellationToken cancellationToken)
    {
        var target = CreateTargetClient(repository);
        return GetTagsAsync(target.Pipeline, target.Endpoint, repository, cancellationToken);
    }

    /// <summary>
    /// Copies one tagged image, including every platform manifest of a multi-platform index,
    /// from the public registry into <paramref name="targetRepository"/> of the deployment registry.
    /// </summary>
    public async Task CopyImageAsync(string repository, string tag, string targetRepository, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!IsValidRepositoryName(repository) || !IsValidRepositoryName(targetRepository))
        {
            throw new ArgumentException("Repository names must use lowercase letters, digits, and single '.', '_', '-', or '/' separators.");
        }
        if (!TagPattern.IsMatch(tag))
        {
            throw new ArgumentException($"Tag '{tag}' is not a valid image tag.", nameof(tag));
        }
        var source = new ContainerRegistryContentClient(SourceEndpoint, repository);
        var target = CreateTargetClient(targetRepository);
        _logger.LogInformation("Image sync started {Source}/{Repository}:{Tag} -> {Target}/{TargetRepository} at {Timestamp}",
            SourceLoginServer, repository, tag, TargetLoginServer, targetRepository, DateTimeOffset.UtcNow);
        await CopyManifestAsync(source, target, tag, tag, progress, cancellationToken);
        _logger.LogInformation("Image sync completed {Source}/{Repository}:{Tag} -> {Target}/{TargetRepository} at {Timestamp}",
            SourceLoginServer, repository, tag, TargetLoginServer, targetRepository, DateTimeOffset.UtcNow);
    }

    /// <summary>True when <paramref name="repository"/> is a valid registry repository name.</summary>
    public static bool IsValidRepositoryName(string? repository) =>
        !string.IsNullOrEmpty(repository) && repository.Length <= 256 && RepositoryNamePattern.IsMatch(repository);

    private ContainerRegistryContentClient CreateTargetClient(string repository)
    {
        if (!HasTarget)
        {
            throw new InvalidOperationException("Set CompanionApp__proxyacr to the deployment registry name before synchronizing images.");
        }
        return new ContainerRegistryContentClient(new Uri($"https://{TargetLoginServer}"), repository, _credential);
    }

    private static async Task CopyManifestAsync(ContainerRegistryContentClient source, ContainerRegistryContentClient target,
        string reference, string? targetTag, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var manifest = (await source.GetManifestAsync(reference, cancellationToken)).Value;
        using var document = JsonDocument.Parse(manifest.Manifest.ToMemory());
        var mediaType = manifest.MediaType.ToString();
        if (document.RootElement.TryGetProperty("mediaType", out var declaredMediaType) && declaredMediaType.ValueKind == JsonValueKind.String)
        {
            mediaType = declaredMediaType.GetString() ?? mediaType;
        }

        if (IndexMediaTypes.Contains(mediaType, StringComparer.Ordinal) || document.RootElement.TryGetProperty("manifests", out _))
        {
            foreach (var child in document.RootElement.GetProperty("manifests").EnumerateArray())
            {
                await CopyManifestAsync(source, target, child.GetProperty("digest").GetString()!, null, progress, cancellationToken);
            }
        }
        else
        {
            var digests = new List<string>();
            if (document.RootElement.TryGetProperty("config", out var config))
            {
                digests.Add(config.GetProperty("digest").GetString()!);
            }
            if (document.RootElement.TryGetProperty("layers", out var layers))
            {
                digests.AddRange(layers.EnumerateArray().Select(layer => layer.GetProperty("digest").GetString()!));
            }
            foreach (var digest in digests.Distinct(StringComparer.Ordinal))
            {
                await CopyBlobAsync(source, target, digest, progress, cancellationToken);
            }
        }

        await target.SetManifestAsync(manifest.Manifest, targetTag, (ManifestMediaType)mediaType, cancellationToken);
        progress?.Report(targetTag is null ? $"Pushed manifest {manifest.Digest}" : $"Pushed {target.RepositoryName}:{targetTag} ({manifest.Digest})");
    }

    private static async Task CopyBlobAsync(ContainerRegistryContentClient source, ContainerRegistryContentClient target,
        string digest, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (await BlobExistsAsync(target, digest, cancellationToken))
        {
            progress?.Report($"Layer {ShortDigest(digest)} already present");
            return;
        }

        // Stage the layer in a seekable temporary file. Uploading straight from the network stream makes the
        // SDK send one PATCH per partial read and leaves the source connection idle until it times out.
        await using var staged = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var download = (await source.DownloadBlobStreamingAsync(digest, cancellationToken)).Value;
        await using (var content = download.Content)
        {
            await content.CopyToAsync(staged, cancellationToken);
        }
        staged.Position = 0;
        var upload = (await target.UploadBlobAsync(staged, cancellationToken)).Value;
        if (!string.Equals(upload.Digest, digest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Uploaded layer digest {upload.Digest} does not match source digest {digest}.");
        }
        progress?.Report($"Copied layer {ShortDigest(digest)} ({upload.SizeInBytes:N0} bytes)");
    }

    private static async Task<bool> BlobExistsAsync(ContainerRegistryContentClient client, string digest, CancellationToken cancellationToken)
    {
        using var message = client.Pipeline.CreateMessage();
        message.Request.Method = RequestMethod.Head;
        message.Request.Uri.Reset(new Uri(client.Endpoint, $"/v2/{client.RepositoryName}/blobs/{digest}"));
        await client.Pipeline.SendAsync(message, cancellationToken);
        return message.Response.Status == 200;
    }

    private static async Task<IReadOnlyList<string>> GetTagsAsync(HttpPipeline pipeline, Uri endpoint, string repository, CancellationToken cancellationToken)
    {
        var tags = new List<string>();
        Uri? next = new(endpoint, $"/v2/{repository}/tags/list?n=100");
        while (next is not null)
        {
            using var message = pipeline.CreateMessage();
            message.Request.Method = RequestMethod.Get;
            message.Request.Uri.Reset(next);
            await pipeline.SendAsync(message, cancellationToken);
            var response = message.Response;
            if (response.Status == 404)
            {
                break;
            }
            if (response.Status != 200)
            {
                throw new RequestFailedException(response);
            }

            using (var document = await JsonDocument.ParseAsync(response.ContentStream!, cancellationToken: cancellationToken))
            {
                if (document.RootElement.TryGetProperty("tags", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    tags.AddRange(values.EnumerateArray().Select(value => value.GetString()).OfType<string>());
                }
            }

            next = response.Headers.TryGetValue("Link", out var link) ? ParseNextLink(endpoint, link) : null;
        }
        tags.Sort(StringComparer.Ordinal);
        return tags;
    }

    private static Uri? ParseNextLink(Uri endpoint, string link)
    {
        var start = link.IndexOf('<');
        var end = link.IndexOf('>');
        if (start < 0 || end <= start || !link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new Uri(endpoint, link[(start + 1)..end]);
    }

    private static string ToLoginServer(string? registry)
    {
        var value = (registry ?? string.Empty).Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["https://".Length..];
        }
        value = value.TrimEnd('/').ToLowerInvariant();
        return value.Length == 0 || value.Contains('.') ? value : $"{value}.azurecr.io";
    }

    private static string ShortDigest(string digest) =>
        digest.Length > 19 ? digest[..19] : digest;
}
