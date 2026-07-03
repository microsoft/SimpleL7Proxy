namespace chat_tester.Components.Shared;

/// <summary>
/// Custom request headers entered on the Headers tab. Registered as a singleton so
/// the values are remembered across page navigation and across every request in a
/// burst. A value may contain the <c>{id}</c> token, which is replaced with the
/// sequential request number when the header is applied.
/// </summary>
public class HeaderSettings
{
    /// <summary>Token replaced with the sequential request number in a header value.</summary>
    public const string IdToken = "{id}";

    /// <summary>A single custom header name/value pair. Mutable so the UI can bind to it.</summary>
    public sealed class HeaderItem
    {
        public string Name { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>The configured custom headers, in display order.</summary>
    public List<HeaderItem> Headers { get; } = new();

    private bool _defaultsApplied;

    /// <summary>
    /// Seeds the header list from configuration the first time it is called. Each
    /// default entry is a <c>Name: Value</c> string. Once seeded (or if the user has
    /// already added headers), later calls are ignored so edits survive navigation.
    /// </summary>
    public void ApplyDefaultsIfMissing(string[]? defaultHeaders)
    {
        if (_defaultsApplied)
        {
            return;
        }

        _defaultsApplied = true;

        if (Headers.Count > 0 || defaultHeaders is not { Length: > 0 })
        {
            return;
        }

        foreach (var line in defaultHeaders)
        {
            var item = Parse(line);
            if (item is not null)
            {
                Headers.Add(item);
            }
        }
    }

    /// <summary>Adds a new empty header row.</summary>
    public void Add() => Headers.Add(new HeaderItem());

    /// <summary>Removes the given header row.</summary>
    public void Remove(HeaderItem item) => Headers.Remove(item);

    /// <summary>Applies every configured header to the request, substituting <c>{id}</c>.</summary>
    public void ApplyHeaders(HttpRequestMessage request, int requestIndex)
    {
        if (request is null)
        {
            return;
        }

        foreach (var item in Headers)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            var value = (item.Value ?? string.Empty)
                .Replace(IdToken, requestIndex.ToString(), StringComparison.OrdinalIgnoreCase);

            request.Headers.Remove(item.Name);
            request.Headers.TryAddWithoutValidation(item.Name, value);
        }
    }

    private static HeaderItem? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separator = line.IndexOf(':');
        if (separator < 0)
        {
            return new HeaderItem { Name = line.Trim() };
        }

        return new HeaderItem
        {
            Name = line[..separator].Trim(),
            Value = line[(separator + 1)..].Trim()
        };
    }
}
