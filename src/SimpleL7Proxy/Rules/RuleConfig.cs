using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Rules;

/// <summary>
/// String matching operators supported by a rule condition.
/// </summary>
public enum MatchOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Regex
}

/// <summary>
/// A single string-match condition evaluated against a named field in the
/// evaluation context (for example a header name, "path", or "method").
/// </summary>
public sealed class RuleCondition
{
    /// <summary>Name of the field in the context to test.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>The string match operator to apply.</summary>
    [JsonPropertyName("match")]
    public MatchOperator Match { get; set; } = MatchOperator.Equals;

    /// <summary>The value to compare the field against.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>When true, comparisons are case-insensitive. Defaults to true.</summary>
    [JsonPropertyName("ignoreCase")]
    public bool IgnoreCase { get; set; } = true;

    [JsonIgnore]
    private Regex? _compiledRegex;

    /// <summary>
    /// Evaluates this condition against the supplied context. Returns false when
    /// the field is not present in the context.
    /// </summary>
    public bool Evaluate(IReadOnlyDictionary<string, string> context)
    {
        if (!context.TryGetValue(Field, out var actual) || actual is null)
        {
            // A missing field only satisfies negated operators.
            return Match is MatchOperator.NotEquals or MatchOperator.NotContains;
        }

        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return Match switch
        {
            MatchOperator.Equals => actual.Equals(Value, comparison),
            MatchOperator.NotEquals => !actual.Equals(Value, comparison),
            MatchOperator.Contains => actual.Contains(Value, comparison),
            MatchOperator.NotContains => !actual.Contains(Value, comparison),
            MatchOperator.StartsWith => actual.StartsWith(Value, comparison),
            MatchOperator.EndsWith => actual.EndsWith(Value, comparison),
            MatchOperator.Regex => GetRegex().IsMatch(actual),
            _ => false
        };
    }

    /// <summary>
    /// Performs all one-time, expensive preparation so that <see cref="Evaluate"/>
    /// is allocation-free on the hot path. Called once by the parser after
    /// deserialization. Compiling the regex here (rather than lazily) also moves
    /// pattern validation to parse time and avoids a compile race under concurrency.
    /// </summary>
    public void Compile()
    {
        _compiledRegex = Match == MatchOperator.Regex
            ? new Regex(
                Value,
                (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled | RegexOptions.CultureInvariant)
            : null;
    }

    private Regex GetRegex()
    {
        // Normally set by Compile() at parse time; this is a defensive fallback
        // for conditions constructed without going through the parser.
        return _compiledRegex ??= new Regex(
            Value,
            (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}

/// <summary>
/// A single if-else rule. When <see cref="If"/> evaluates to true the rule
/// yields <see cref="Then"/>, otherwise it yields <see cref="Else"/>.
/// </summary>
public sealed class Rule
{
    /// <summary>Optional friendly name used for diagnostics.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>The condition to evaluate.</summary>
    [JsonPropertyName("if")]
    public RuleCondition If { get; set; } = new();

    /// <summary>
    /// Collection of key-value pairs applied when the condition is true.
    /// </summary>
    [JsonPropertyName("then")]
    public Dictionary<string, string>? Then { get; set; }

    /// <summary>
    /// Collection of key-value pairs applied when the condition is false.
    /// </summary>
    [JsonPropertyName("else")]
    public Dictionary<string, string>? Else { get; set; }

    /// <summary>
    /// Evaluates the rule and returns the matching branch's key-value pairs, or
    /// null when the non-matching branch is not defined.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Evaluate(IReadOnlyDictionary<string, string> context)
        => If.Evaluate(context) ? Then : Else;

    /// <summary>
    /// Prepares the rule for high-throughput evaluation. Called once by the parser.
    /// </summary>
    public void Compile() => If.Compile();
}

/// <summary>
/// An in-memory set of if-else string-match rules.
/// Use <see cref="RuleConfigParser"/> to build an instance from JSON and
/// <see cref="RuleProcessor"/> to evaluate it against a context.
/// </summary>
public sealed class RuleConfig
{
    /// <summary>The ordered set of rules parsed from the JSON definition.</summary>
    [JsonPropertyName("rules")]
    public List<Rule> Rules { get; set; } = new();

    /// <summary>
    /// Prepares every rule for high-throughput evaluation (for example compiling
    /// regular expressions). Called once by the parser after deserialization so
    /// the per-request evaluation path performs no one-time work.
    /// </summary>
    public void Compile()
    {
        foreach (var rule in Rules)
        {
            rule.Compile();
        }
    }
}

