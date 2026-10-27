using System.Collections.Frozen;

namespace SimpleL7Proxy.Tokenomics;

/// <summary>Lists default partial URLs keyed by the API names used in Model &amp; API Routing.</summary>
public static class TokenomicsApiPaths {
    /// <summary>Gets the case-insensitive API path catalog, retaining model placeholders and query strings.</summary>
    public static FrozenDictionary<string, string> PartialUrls { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["OpenAI Chat Completions"] = "/openai/v1/chat/completions",
            ["OpenAI Responses"] = "/openai/v1/responses",
            ["Anthropic Messages"] = "/v1/messages",
            ["Gemini generateContent"] = "/v1beta/models/{model}:generateContent",
            ["Gemini (OpenAI-compatible)"] = "/v1beta/openai/chat/completions",
            ["Cohere (OpenAI-compatible)"] = "/compatibility/v1/chat/completions",
            ["Cohere v2 Chat"] = "/v2/chat",
            ["Cohere v1 Chat"] = "/v1/chat",
            ["SystemOne"] = "/providers/microsoft/v1/systemone",
            ["OpenAI Chat Completions (vision)"] = "/openai/v1/chat/completions",
            ["OpenAI Responses (vision)"] = "/openai/v1/responses",
            ["Anthropic Messages (vision)"] = "/v1/messages",
            ["Gemini generateContent (vision)"] = "/v1beta/models/{model}:generateContent",
            ["Azure AI Vision Image Analysis"] = "/computervision/imageanalysis:analyze?api-version=2024-02-01"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly Uri _baseUri = new("https://api.invalid/");
    private static readonly (string Path, string Name)[] _catalogPaths = PartialUrls
        .OrderBy(api => api.Key.EndsWith(" (vision)", StringComparison.OrdinalIgnoreCase))
        .Select(api => (Path: Uri.UnescapeDataString(new Uri(_baseUri, api.Value).AbsolutePath).TrimEnd('/'), Name: api.Key))
        .DistinctBy(api => api.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private static readonly FrozenDictionary<string, string> _apiNamesByPath = _catalogPaths
        .Where(api => !api.Path.Contains("{model}", StringComparison.Ordinal))
        .ToFrozenDictionary(api => api.Path, api => api.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly (string Prefix, string Suffix, string Name)[] _templates = _catalogPaths
        .Where(api => api.Path.Contains("{model}", StringComparison.Ordinal))
        .Select(api => {
            var modelIndex = api.Path.IndexOf("{model}", StringComparison.Ordinal);
            return (api.Path[..modelIndex], api.Path[(modelIndex + "{model}".Length)..], api.Name);
        })
        .ToArray();

    /// <summary>Returns the API name for an absolute or relative HTTP URI, or null when no catalog path matches.</summary>
    /// <remarks>Catalog paths may have surrounding characters. Query strings and fragments are ignored. 
    /// The longest fixed path wins before model templates, whose placeholders match one nonempty path segment. 
    /// Shared chat/vision paths return the non-vision name.</remarks>
    public static string? GetApiName(string? inputUri) {
        if (string.IsNullOrWhiteSpace(inputUri)) {
            return null;
        }

        var input = inputUri.AsSpan();
        if (input[0] == '/' && !input.StartsWith("//", StringComparison.Ordinal)
            && input.IndexOfAny('%', '\\') < 0
            && input.IndexOfAny('\r', '\n', '\t') < 0
            && !input.Contains("/.", StringComparison.Ordinal)
            && !char.IsWhiteSpace(input[^1])) {
            var queryIndex = input.IndexOfAny('?', '#');
            return GetApiNameFromPath(queryIndex < 0 ? input : input[..queryIndex]);
        }

        if (!Uri.TryCreate(_baseUri, inputUri, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
            return null;
        }

        return GetApiNameFromPath(Uri.UnescapeDataString(uri.AbsolutePath));
    }

    /// <summary>Returns the API name for an already-decoded, normalized request path without allocating, or null when no path matches.</summary>
    /// <remarks>The path must exclude any query string or fragment. Catalog paths may have surrounding characters; the longest fixed match wins before model templates. Use GetApiName for URI normalization and percent decoding.</remarks>
    public static string? GetApiNameFromPath(ReadOnlySpan<char> path) {
        path = path.TrimEnd('/');
        if (_apiNamesByPath.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(path, out var apiName)) {
            return apiName;
        }

        foreach (var (prefix, suffix, name) in _templates) {
            if (path.Length > prefix.Length + suffix.Length
                && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !path.Slice(prefix.Length, path.Length - prefix.Length - suffix.Length).Contains('/')) {
                return name;
            }
        }

        string? matchedApi = null;
        var matchedLength = 0;
        foreach (var (apiPath, name) in _apiNamesByPath) {
            if (apiPath.Length >= matchedLength
                && path.IndexOf(apiPath, StringComparison.OrdinalIgnoreCase) >= 0
                && (apiPath.Length > matchedLength
                    || string.Compare(name, matchedApi, StringComparison.OrdinalIgnoreCase) < 0)) {
                matchedApi = name;
                matchedLength = apiPath.Length;
            }
        }

        if (matchedApi is not null) {
            return matchedApi;
        }

        foreach (var (prefix, suffix, name) in _templates) {
            var remaining = path;
            while (true) {
                var prefixIndex = remaining.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (prefixIndex < 0) {
                    break;
                }

                remaining = remaining[(prefixIndex + prefix.Length)..];
                var suffixIndex = remaining.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (suffixIndex > 0 && !remaining[..suffixIndex].Contains('/')) {
                    return name;
                }
            }
        }

        return null;
    }
}