namespace SimpleL7Proxy.Rules;

/// <summary>
/// Runs a <see cref="RuleConfig"/> against a dictionary of key-value pairs and
/// returns the matching rule results.
/// </summary>
public sealed class RuleProcessor
{
    private readonly RuleConfig _config;

    /// <summary>
    /// Creates a processor for the supplied rule configuration.
    /// </summary>
    public RuleProcessor(RuleConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    /// <summary>
    /// Evaluates every rule against the context and returns each rule's
    /// key-value result in order. Rules without a matching branch are skipped.
    /// </summary>
    public IEnumerable<IReadOnlyDictionary<string, string>> Process(
        IReadOnlyDictionary<string, string> context,
        short? s7PHash = null,
        ICollection<string>? matchedRuleNames = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var rule in _config.Rules)
        {
            var matched = rule.If.Evaluate(context, s7PHash);
            var result = matched ? rule.Then : rule.Else;
            if (result is not null)
            {
                if (!string.IsNullOrEmpty(rule.Name))
                {
                    matchedRuleNames?.Add(matched ? rule.Name : rule.Name + "-else");
                }

                yield return result;
            }
        }
    }

    /// <summary>
    /// Evaluates rules in order and returns the first non-null key-value result,
    /// or <paramref name="defaultResult"/> when no rule produces a result.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ProcessFirst(
        IReadOnlyDictionary<string, string> context,
        short? s7PHash = null,
        IReadOnlyDictionary<string, string>? defaultResult = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var rule in _config.Rules)
        {
            var result = rule.Evaluate(context, s7PHash);
            if (result is not null)
            {
                return result;
            }
        }

        return defaultResult;
    }
}
