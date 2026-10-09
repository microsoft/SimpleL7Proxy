namespace SimpleL7Proxy.Extensions;

/// <summary>
/// Provides helpers for parsing list and key-value configuration values.
/// </summary>
public static class ConfigParsingExtensions
{
    /// <summary>
    /// Converts colon-delimited integer pairs into a dictionary.
    /// </summary>
    public static Dictionary<int, int> KVIntPairs(this IEnumerable<string> values)
    {
        Dictionary<int, int> keyValuePairs = [];

        foreach (var item in values)
        {
            var kvp = item.Split(':');
            if (kvp.Length == 2 && int.TryParse(kvp[0], out int key) && int.TryParse(kvp[1], out int value))
            {
                keyValuePairs[key] = value;
            }
        }

        return keyValuePairs;
    }

    /// <summary>
    /// Converts delimited string pairs into a dictionary.
    /// </summary>
    public static Dictionary<string, string> KVStringPairs(this IEnumerable<string> values, char delimiter = '=')
    {
        char fallback = delimiter == '=' ? ':' : '=';
        Dictionary<string, string> keyValuePairs = [];

        foreach (var item in values)
        {
            var kvp = item.Split(delimiter, 2);
            if (kvp.Length == 2)
            {
                keyValuePairs[kvp[0].Trim()] = kvp[1].Trim();
                continue;
            }

            kvp = item.Split(fallback, 2);
            if (kvp.Length == 2)
            {
                keyValuePairs[kvp[0].Trim()] = kvp[1].Trim();
            }
        }

        return keyValuePairs;
    }

    /// <summary>
    /// Converts a comma-delimited value into a list of strings.
    /// </summary>
    public static List<string> ToListOfString(this string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return [.. trimmed.Split(',').Select(part => part.Trim().Trim('"')).Where(part => part.Length > 0)];
    }

    /// <summary>
    /// Converts a comma-delimited value into a list of integers.
    /// </summary>
    public static List<int> ToListOfInt(this string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Split(',').Select(part => int.Parse(part.Trim().Trim('"'))).ToList();
    }
}