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
    Between,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Regex
}

/// <summary>
/// Boundary inclusion modes supported by <see cref="MatchOperator.Between"/>.
/// </summary>
public enum RangeMode
{
    InOpenClosedRange,
    InClosedOpenRange,
    InOpenRange,
    InClosedRange
}

/// <summary>
/// A single condition evaluated against a named field in the
/// evaluation context (for example a header name, "path", or "method").
/// </summary>
public sealed class RuleCondition
{
    private const string HashFieldPrefix = "Hash:";

    /// <summary>Name used when this condition is part of a matched path.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the field in the context to test.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>The match or comparison operator to apply.</summary>
    [JsonPropertyName("match")]
    public MatchOperator Match { get; set; } = MatchOperator.Equals;

    /// <summary>The value to compare the field against.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    /// <summary>The upper bound used by <see cref="MatchOperator.Between"/>.</summary>
    [JsonPropertyName("value2")]
    public string Value2 { get; set; } = string.Empty;

    /// <summary>
    /// Controls which bounds are included by <see cref="MatchOperator.Between"/>.
    /// Defaults to a closed range for backward compatibility.
    /// </summary>
    [JsonPropertyName("mode")]
    public RangeMode Mode { get; set; } = RangeMode.InClosedRange;

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
        decimal expectedNumber2 = default;

        if (Match is MatchOperator.GreaterThan
                or MatchOperator.GreaterThanOrEqual
                or MatchOperator.LessThan
                or MatchOperator.LessThanOrEqual
                or MatchOperator.Between
            && (!decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out actualNumber)
                || !decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedNumber)))
        {
            return false;
        }

        if (Match == MatchOperator.Between
            && (!decimal.TryParse(Value2, NumberStyles.Float, CultureInfo.InvariantCulture, out expectedNumber2)
                || expectedNumber > expectedNumber2))
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
            MatchOperator.Between => Mode switch
            {
                RangeMode.InOpenClosedRange => actualNumber > expectedNumber && actualNumber <= expectedNumber2,
                RangeMode.InClosedOpenRange => actualNumber >= expectedNumber && actualNumber < expectedNumber2,
                RangeMode.InOpenRange => actualNumber > expectedNumber && actualNumber < expectedNumber2,
                RangeMode.InClosedRange => actualNumber >= expectedNumber && actualNumber <= expectedNumber2,
                _ => false
            },
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
/// An ordered named condition and branch evaluated after a rule's primary condition fails.
/// </summary>
public sealed class RuleElseIf
{
    /// <summary>Name used when this elseif clause is part of a matched path.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The condition to evaluate.</summary>
    [JsonPropertyName("if")]
    public RuleCondition If { get; set; } = new();

    /// <summary>The named branch evaluated when the condition matches.</summary>
    [JsonPropertyName("then")]
    public RuleNode? Then { get; set; }

    internal void Compile(int depth)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Every elseif clause must have a name.");
        }

        if (string.IsNullOrWhiteSpace(If.Name))
        {
            throw new ArgumentException($"Elseif clause '{Name}' must give its if condition a name.");
        }

        if (Then is null)
        {
            throw new ArgumentException($"Elseif clause '{Name}' must define a then branch.");
        }

        If.Compile();
        Then.Compile(depth);
    }
}

/// <summary>
/// A named output leaf or recursive if-elseif-else node.
/// </summary>
public class RuleNode
{
    private const int MaxDepth = 16;

    /// <summary>Name used when this node is part of a matched path.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Terminal key-value output. Mutually exclusive with <see cref="If"/>.</summary>
    [JsonPropertyName("set")]
    public Dictionary<string, string>? Set { get; set; }

    /// <summary>The primary condition for a conditional node.</summary>
    [JsonPropertyName("if")]
    public RuleCondition? If { get; set; }

    /// <summary>The named branch evaluated when <see cref="If"/> matches.</summary>
    [JsonPropertyName("then")]
    public RuleNode? Then { get; set; }

    /// <summary>Ordered conditions evaluated when <see cref="If"/> is false.</summary>
    [JsonPropertyName("elseif")]
    public List<RuleElseIf> ElseIf { get; set; } = new();

    /// <summary>The named fallback branch evaluated when no condition matches.</summary>
    [JsonPropertyName("else")]
    public RuleNode? Else { get; set; }

    internal IReadOnlyDictionary<string, string>? Evaluate(
        IReadOnlyDictionary<string, string> context,
        short? s7PHash,
        List<string>? matchedPath)
    {
        matchedPath?.Add(Name);

        if (Set is not null)
        {
            return Set;
        }

        if (If is null)
        {
            return null;
        }

        if (If.Evaluate(context, s7PHash))
        {
            matchedPath?.Add(If.Name);
            return Then?.Evaluate(context, s7PHash, matchedPath);
        }

        for (var index = 0; index < ElseIf.Count; index++)
        {
            var clause = ElseIf[index];
            if (clause.If.Evaluate(context, s7PHash))
            {
                matchedPath?.Add(clause.Name);
                matchedPath?.Add(clause.If.Name);
                return clause.Then?.Evaluate(context, s7PHash, matchedPath);
            }
        }

        return Else?.Evaluate(context, s7PHash, matchedPath);
    }

    /// <summary>
    /// Validates and prepares this node recursively for high-throughput evaluation.
    /// </summary>
    public void Compile() => Compile(0);

    internal void Compile(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ArgumentException($"Rule nesting cannot exceed {MaxDepth} levels.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Every rule node must have a name.");
        }

        var hasSet = Set is not null;
        var hasCondition = If is not null;
        if (hasSet == hasCondition)
        {
            throw new ArgumentException($"Rule node '{Name}' must define exactly one of set or if.");
        }

        if (hasSet)
        {
            if (Then is not null || Else is not null || ElseIf.Count > 0)
            {
                throw new ArgumentException($"Set node '{Name}' cannot define then, elseif, or else.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(If!.Name))
        {
            throw new ArgumentException($"Rule node '{Name}' must give its if condition a name.");
        }

        if (Then is null)
        {
            throw new ArgumentException($"Conditional node '{Name}' must define a then branch.");
        }

        If.Compile();
        Then.Compile(depth + 1);

        foreach (var clause in ElseIf)
        {
            clause.Compile(depth + 1);
        }

        Else?.Compile(depth + 1);
    }
}

/// <summary>A top-level named rule node.</summary>
public sealed class Rule : RuleNode;

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

