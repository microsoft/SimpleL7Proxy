namespace chat_tester.Components.Shared;

/// <summary>
/// Shared HTTP helpers used by the test pages to build request URIs, parse the
/// editable target-URL lists, and apply the debug header consistently.
/// </summary>
public static class ChatTesterHttp
{
    private static readonly string[] LineSeparators = { "\r\n", "\n" };

    /// <summary>Media type used for JSON request payloads.</summary>
    public const string JsonContentType = "application/json";

    /// <summary>Media type requested for server-sent event streams.</summary>
    public const string EventStreamContentType = "text/event-stream";

    /// <summary>Standard HTTP <c>Accept</c> header name.</summary>
    public const string AcceptHeaderName = "Accept";

    /// <summary>
    /// Combines a server base URL and an endpoint path into an absolute URI,
    /// trimming any duplicate slash between them.
    /// </summary>
    public static Uri BuildUri(string serverBaseUrl, string endpointPath)
    {
        return new Uri($"{serverBaseUrl.TrimEnd('/')}{NormalizePath(endpointPath)}");
    }

    /// <summary>
    /// Creates a request whose method is parsed from a <c>"GET"</c>/<c>"POST"</c>
    /// string. For POST requests an optional JSON payload is attached.
    /// </summary>
    public static HttpRequestMessage CreateRequest(string method, Uri uri, string? payload = null, string contentType = JsonContentType)
    {
        var isPost = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        var request = new HttpRequestMessage(isPost ? HttpMethod.Post : HttpMethod.Get, uri);
        if (isPost && payload is not null)
        {
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, contentType);
        }

        return request;
    }

    /// <summary>Adds a header to a request when both the name and value are present.</summary>
    public static void TryApplyHeader(HttpRequestMessage request, string? headerName, string? headerValue)
    {
        if (request is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(headerName) && !string.IsNullOrWhiteSpace(headerValue))
        {
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
    }

    /// <summary>Builds a sorted, human-readable summary of a request's headers (including content headers).</summary>
    public static string SummarizeRequestHeaders(HttpRequestMessage request)
    {
        var builder = new System.Text.StringBuilder();
        AppendHeaderValues(builder, request.Headers);
        if (request.Content is not null)
        {
            AppendHeaderValues(builder, request.Content.Headers);
        }

        return builder.Length == 0 ? "No request headers." : builder.ToString().TrimEnd();
    }

    /// <summary>Builds a sorted, human-readable summary of a response's headers (including content headers).</summary>
    public static string SummarizeResponseHeaders(HttpResponseMessage response)
    {
        var builder = new System.Text.StringBuilder();
        AppendHeaderValues(builder, response.Headers);
        AppendHeaderValues(builder, response.Content.Headers);
        return builder.Length == 0 ? "No response headers." : builder.ToString().TrimEnd();
    }

    private static void AppendHeaderValues(System.Text.StringBuilder builder, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }
    }

    /// <summary>
    /// Builds a target URI for a single probe request, appending a
    /// <c>session</c> query parameter so each request is distinguishable.
    /// </summary>
    public static Uri BuildSessionEndpoint(string serverBaseUrl, string targetPath, int session)
    {
        var normalizedPath = NormalizePath(targetPath);
        var suffix = normalizedPath.Contains('?', StringComparison.Ordinal)
            ? $"&session={session}"
            : $"?session={session}";
        return new Uri($"{serverBaseUrl.TrimEnd('/')}{normalizedPath}{suffix}");
    }

    /// <summary>
    /// Splits the editable, newline-separated target list into normalized,
    /// non-empty paths (each guaranteed to start with <c>/</c>).
    /// </summary>
    public static List<string> ParseTargetLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        return text
            .Split(LineSeparators, StringSplitOptions.None)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(NormalizePath)
            .ToList();
    }

    /// <summary>Adds the debug header to a request when debugging is enabled.</summary>
    public static void ApplyDebugHeader(HttpRequestMessage request, RequestDebugSettings debugSettings)
    {
        if (debugSettings.DebugEnabled)
        {
            request.Headers.TryAddWithoutValidation(RequestDebugSettings.DebugHeaderName, RequestDebugSettings.DebugHeaderValue);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        return path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
    }
}
