using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SimpleL7Proxy.Rules;

/// <summary>
/// String matching and numeric comparison operators supported by a rule condition.
/// </summary>
public enum MatchOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Regex
}

/// <summary>
/// A single condition evaluated against a named field in the
/// evaluation context (for example a header name, "path", or "method").
/// </summary>
public sealed class RuleCondition
{
    private const string HashFieldPrefix = "Hash:";

    /// <summary>Name of the field in the context to test.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>The match or comparison operator to apply.</summary>
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

    [JsonIgnore]
    private string? _hashedField;

    /// <summary>
    /// Evaluates this condition against the supplied context. Returns false when
    /// the field is not present in the context.
    /// </summary>
    public bool Evaluate(IReadOnlyDictionary<string, string> context, short? s7PHash = null)
    {
        Span<char> computedValueBuffer = stackalloc char[6];

        if (s7PHash.HasValue && string.Equals(Field, "S7PHash", StringComparison.OrdinalIgnoreCase))
        {
            s7PHash.Value.TryFormat(computedValueBuffer, out var charsWritten, provider: CultureInfo.InvariantCulture);
            return EvaluateActual(computedValueBuffer[..charsWritten]);
        }

        if (GetHashedField() is { } hashedField)
        {
            short bucket;

            if (s7PHash.HasValue && string.Equals(hashedField, "S7PHash", StringComparison.OrdinalIgnoreCase))
            {
                s7PHash.Value.TryFormat(computedValueBuffer, out var charsWritten, provider: CultureInfo.InvariantCulture);
                bucket = RuleHash.CalculateBucket(computedValueBuffer[..charsWritten]);
            }
            else if (context.TryGetValue(hashedField, out var contextValue) && contextValue is not null)
            {
                bucket = RuleHash.CalculateBucket(contextValue.AsSpan());
            }
            else
            {
                return Match is MatchOperator.NotEquals or MatchOperator.NotContains;
            }

            bucket.TryFormat(computedValueBuffer, out var bucketCharsWritten, provider: CultureInfo.InvariantCulture);
            return EvaluateActual(computedValueBuffer[..bucketCharsWritten]);
        }

        if (context.TryGetValue(Field, out var actual) && actual is not null)
        {
            return EvaluateActual(actual.AsSpan());
        }

        // A missing field only satisfies negated operators.
        return Match is MatchOperator.NotEquals or MatchOperator.NotContains;
    }

    private bool EvaluateActual(ReadOnlySpan<char> actual)
    {
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        decimal actualNumber = default;
        decimal expectedNumber = default;

        if (Match is MatchOperator.GreaterThan
                or MatchOperator.GreaterThanOrEqual
                or MatchOperator.LessThan
                or MatchOperator.LessThanOrEqual
            && (!decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out actualNumber)
                || !decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedNumber)))
        {
            return false;
        }

        return Match switch
        {
            MatchOperator.Equals => actual.Equals(Value.AsSpan(), comparison),
            MatchOperator.NotEquals => !actual.Equals(Value.AsSpan(), comparison),
            MatchOperator.GreaterThan => actualNumber > expectedNumber,
            MatchOperator.GreaterThanOrEqual => actualNumber >= expectedNumber,
            MatchOperator.LessThan => actualNumber < expectedNumber,
            MatchOperator.LessThanOrEqual => actualNumber <= expectedNumber,
            MatchOperator.Contains => actual.Contains(Value.AsSpan(), comparison),
            MatchOperator.NotContains => !actual.Contains(Value.AsSpan(), comparison),
            MatchOperator.StartsWith => actual.StartsWith(Value.AsSpan(), comparison),
            MatchOperator.EndsWith => actual.EndsWith(Value.AsSpan(), comparison),
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
        _hashedField = Field.Length > HashFieldPrefix.Length
            && Field.StartsWith(HashFieldPrefix, StringComparison.OrdinalIgnoreCase)
                ? Field[HashFieldPrefix.Length..]
                : string.Empty;

        _compiledRegex = Match == MatchOperator.Regex
            ? new Regex(
                Value,
                (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled | RegexOptions.CultureInvariant)
            : null;
    }

    private string? GetHashedField()
    {
        if (_hashedField is not null)
        {
            return _hashedField.Length == 0 ? null : _hashedField;
        }

        return Field.Length > HashFieldPrefix.Length
            && Field.StartsWith(HashFieldPrefix, StringComparison.OrdinalIgnoreCase)
                ? Field[HashFieldPrefix.Length..]
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
    public IReadOnlyDictionary<string, string>? Evaluate(IReadOnlyDictionary<string, string> context, short? s7PHash = null)
        => If.Evaluate(context, s7PHash) ? Then : Else;

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

