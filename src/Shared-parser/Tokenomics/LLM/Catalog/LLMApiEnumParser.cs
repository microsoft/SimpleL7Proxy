using System.Collections.Frozen;

namespace SharedParser.Tokenomics.LLM.Catalog;

/// <summary>Converts catalog strings and request paths to API identities using CatalogAPI metadata.</summary>
public static class LLMApiEnumParser {
    /// <summary>Gets the immutable catalog entries keyed by API identity.</summary>
    public static FrozenDictionary<LLMApiEnum, CatalogAPI> APIs { get; } = CatalogAPI.APIs;

    /// <summary>Gets the default API paths, retaining model placeholders and query strings.</summary>
    public static FrozenDictionary<LLMApiEnum, string> PartialUrls { get; } =
        APIs.ToFrozenDictionary(api => api.Key, api => api.Value.Path);

    private static readonly FrozenDictionary<string, LLMApiEnum> _apis = APIs.Values
        .SelectMany(api => new[] { api.DisplayName, api.ShortName }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new KeyValuePair<string, LLMApiEnum>(name, api.API)))
        .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Uri _pathBaseUri = new("https://api.invalid/");
    private static readonly FrozenDictionary<string, LLMApiEnum> _apisByPath = APIs.Values
        .OrderBy(api => api.API != api.Family)
        .SelectMany(api => new[] { api.Path }.Concat(api.PathAliases)
            .Select(path => (Path: Uri.UnescapeDataString(new Uri(_pathBaseUri, path).AbsolutePath).TrimEnd('/'), Api: api.API)))
        .DistinctBy(api => api.Path, StringComparer.OrdinalIgnoreCase)
        .ToFrozenDictionary(api => api.Path, api => api.Api, StringComparer.OrdinalIgnoreCase);
    private static readonly (string Path, string Prefix, string Suffix, LLMApiEnum Api)[] _apiPaths = _apisByPath
        .Select(entry => {
            var path = entry.Key;
            var placeholderIndex = path.IndexOf("{model}", StringComparison.Ordinal);
            return (Path: path,
                Prefix: placeholderIndex < 0 ? string.Empty : path[..placeholderIndex],
                Suffix: placeholderIndex < 0 ? string.Empty : path[(placeholderIndex + "{model}".Length)..],
                Api: entry.Value);
        })
        .OrderByDescending(entry => entry.Path.Length)
        .ThenBy(entry => entry.Api)
        .ToArray();

    /// <summary>Parses a catalog display name or ID, trimming surrounding whitespace.</summary>
    /// <param name="value">The API display name or catalog ID.</param>
    /// <param name="api">The matching API, or Unknown when parsing fails.</param>
    /// <returns>True when the input identifies a known catalog API; otherwise, false.</returns>
    public static bool TryParse(string? value, out LLMApiEnum api) {
        api = LLMApiEnum.Unknown;
        return value is not null
            && _apis.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(value.AsSpan().Trim(), out api);
    }

    /// <summary>Parses an HTTP URL or path ending in a default catalog API path.</summary>
    /// <remarks>Matching is case-insensitive and ignores query strings, fragments, and trailing slashes.
    /// Exact catalog paths and aliases use a frozen-dictionary lookup before longest-first suffix matching.
    /// Model placeholders match one nonempty path segment.
    /// Shared chat/vision paths return the non-vision API. Ordinary unescaped request paths
    /// use allocation-free span matching; other inputs use URI normalization.</remarks>
    /// <param name="value">The HTTP URL or request path.</param>
    /// <param name="api">The matching API, or Unknown when parsing fails.</param>
    /// <returns>True when the path ends in a default API path; otherwise, false.</returns>
    public static bool TryParsePath(string? value, out LLMApiEnum api) {
        api = LLMApiEnum.Unknown;
        var input = value.AsSpan().Trim();
        if (input.IsEmpty) {
            return false;
        }

        ReadOnlySpan<char> path;
        if (input[0] == '/' && !input.StartsWith("//", StringComparison.Ordinal)
            && input.IndexOfAny('%', '\\') < 0
            && input.IndexOfAny('\r', '\n', '\t') < 0
            && !input.Contains("/.", StringComparison.Ordinal)) {
            var queryIndex = input.IndexOfAny('?', '#');
            path = queryIndex < 0 ? input : input[..queryIndex];
        }
        else {
            if (!Uri.TryCreate(_pathBaseUri, value!.Trim(), out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
                return false;
            }
            path = Uri.UnescapeDataString(uri.AbsolutePath).AsSpan();
        }

        path = path.TrimEnd('/');
        if (_apisByPath.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(path, out api)) {
            return true;
        }

        foreach (var (defaultPath, prefix, suffix, candidate) in _apiPaths) {
            if (prefix.Length == 0) {
                if (path.EndsWith(defaultPath, StringComparison.OrdinalIgnoreCase)) {
                    api = candidate;
                    return true;
                }
                continue;
            }

            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var modelEnd = path.Length - suffix.Length;
            var modelStart = path[..modelEnd].LastIndexOf('/') + 1;
            if (modelEnd > modelStart
                && path[..modelStart].EndsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                api = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the canonical catalog ID, or an empty string for an unspecified API.</summary>
    /// <param name="api">The API identity.</param>
    /// <returns>The API catalog ID.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The API value is not defined.</exception>
    public static string ToId(LLMApiEnum api) => api == LLMApiEnum.Unknown ? string.Empty
        : APIs.TryGetValue(api, out var entry) ? entry.ShortName
        : throw new ArgumentOutOfRangeException(nameof(api), api, "Undefined API identity.");

    /// <summary>Returns the canonical catalog display name, or "Unknown" for an unspecified API.</summary>
    /// <param name="api">The API identity.</param>
    /// <returns>The API display name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The API value is not defined.</exception>
    public static string ToDisplayName(LLMApiEnum api) => api == LLMApiEnum.Unknown ? "Unknown"
        : APIs.TryGetValue(api, out var entry) ? entry.DisplayName
        : throw new ArgumentOutOfRangeException(nameof(api), api, "Undefined API identity.");
}