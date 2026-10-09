using System.Collections.Immutable;
using System.Globalization;

namespace SimpleL7Proxy.Tokenomics.Llm;

public static class LLMHandler
{
    public readonly record struct UsageResult(
        UsageProvider Provider,
        long PromptTokens,
        long CachedTokens,
        long CompletionTokens,
        long TotalTokens,
        long ReasoningTokens,
        long CacheCreationTokens);

    public static LLMStats PopulateUsage( UsageProvider provider, IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var fieldSnapshot = fields as IReadOnlyList<KeyValuePair<string, string>> ?? fields.ToArray();

        if (!TryGetUsage(provider, fieldSnapshot, out var usage))
        {
            return new LLMStats();
        }

        return new LLMStats
        {
            InputTokens = ToInt32(usage.PromptTokens),
            OutputTokens = ToInt32(usage.CompletionTokens),
            CachedTokens = ToInt32(usage.CachedTokens),
            IsJailbreakDetected = ReadBoolean(
                fieldSnapshot,
                "Usage.Is_Jailbreak_Detected"),
            IsContentFiltered = ReadBoolean(
                fieldSnapshot,
                "Usage.Is_Content_Filtered")
        };
    }

    private static bool ReadBoolean(
        IReadOnlyList<KeyValuePair<string, string>> fields,
        string path)
    {
        foreach (var field in fields)
        {
            if (MatchesPath(field.Key, path) &&
                bool.TryParse(field.Value, out var value))
            {
                return value;
            }
        }

        return false;
    }

    private static int ToInt32(long value)
    {
        return value switch
        {
            < 0 => 0,
            > int.MaxValue => int.MaxValue,
            _ => (int)value
        };
    }
    public static bool TryGetUsage(
        string model,
        IEnumerable<KeyValuePair<string, string>> fields,
        out UsageResult usage)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var fieldSnapshot = fields as IReadOnlyList<KeyValuePair<string, string>>
            ?? fields.ToArray();

        var detectedProvider = ModelMap.GetUsageProvider(model);

        if (detectedProvider != UsageProvider.Unknown &&
            TryGetUsage(
                detectedProvider,
                fieldSnapshot,
                out usage))
        {
            return true;
        }

        foreach (var provider in Enum.GetValues<UsageProvider>())
        {
            if (provider == UsageProvider.Unknown ||
                provider == detectedProvider)
            {
                continue;
            }

            if (TryGetUsage(provider, fieldSnapshot, out usage))
            {
                return true;
            }
        }

        usage = default;
        return false;
    }

    private static bool TryGetUsage(
        UsageProvider provider,
        IReadOnlyList<KeyValuePair<string, string>> fields,
        out UsageResult usage)
    {
        usage = default;

        if (!ModelMap.TryGetUsageFields(provider, out var fieldMap))
        {
            return false;
        }

        var promptFound = TryReadMaximum( fields, fieldMap.PromptTokens, out var promptTokens);
        var cachedFound = TryReadMaximum( fields, fieldMap.CachedTokens, out var cachedTokens);
        var completionFound = TryReadMaximum( fields, fieldMap.CompletionTokens, out var completionTokens);
        var totalFound = TryReadMaximum( fields, fieldMap.TotalTokens, out var totalTokens);
        var reasoningFound = TryReadMaximum( fields, fieldMap.ReasoningTokens,out var reasoningTokens);
        var cacheCreationFound = TryReadMaximum( fields, fieldMap.CacheCreationTokens, out var cacheCreationTokens);

        if (!promptFound && !cachedFound && !completionFound && !totalFound && !reasoningFound && !cacheCreationFound)
        {
            return false;
        }

        if (fieldMap.AddCacheTokensToPrompt)
        {
            promptTokens += cachedTokens + cacheCreationTokens;
        }

        if (fieldMap.AddReasoningTokensToCompletion)
        {
            completionTokens += reasoningTokens;
        }

        if (!totalFound)
        {
            totalTokens = promptTokens + completionTokens;
        }

        usage = new UsageResult( provider, promptTokens, cachedTokens, completionTokens, totalTokens,
            reasoningTokens, cacheCreationTokens);

        return true;
    }

    private static bool TryReadMaximum(
        IReadOnlyList<KeyValuePair<string, string>> fields,
        ImmutableArray<string> paths,
        out long value)
    {
        value = 0;
        var found = false;

        foreach (var path in paths)
        {
            foreach (var field in fields)
            {
                if (!MatchesPath(field.Key, path) ||
                    !long.TryParse(
                        field.Value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsedValue))
                {
                    continue;
                }

                value = found
                    ? Math.Max(value, parsedValue)
                    : parsedValue;

                found = true;
            }
        }

        return found;
    }

    private static bool MatchesPath(string fieldName, string path)
    {
        if (fieldName.Equals(
            path,
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fieldName.Length > path.Length &&
            fieldName.EndsWith(
                path,
                StringComparison.OrdinalIgnoreCase) &&
            fieldName[fieldName.Length - path.Length - 1] == '.';
    }

    private static string Format(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}