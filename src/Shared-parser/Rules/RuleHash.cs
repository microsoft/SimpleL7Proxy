namespace SimpleL7Proxy.Rules;

/// <summary>
/// Produces deterministic buckets for percentage-based rules.
/// </summary>
public static class RuleHash
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    /// <summary>
    /// Hashes one value into a bucket from 0 through 99.
    /// </summary>
    public static short CalculateBucket(ReadOnlySpan<char> value)
    {
        var hash = OffsetBasis;
        Append(ref hash, value);
        return (short)(hash % 100);
    }

    /// <summary>
    /// Hashes two distinct values into a bucket from 0 through 99.
    /// </summary>
    public static short CalculateBucket(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        var hash = OffsetBasis;
        Append(ref hash, first);

        unchecked
        {
            hash = (hash ^ '\n') * Prime;
        }

        Append(ref hash, second);
        return (short)(hash % 100);
    }

    private static void Append(ref uint hash, ReadOnlySpan<char> value)
    {
        unchecked
        {
            foreach (var character in value)
            {
                hash = (hash ^ character) * Prime;
            }
        }
    }
}