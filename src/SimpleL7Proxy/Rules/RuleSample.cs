namespace SimpleL7Proxy.Rules;

/// <summary>
/// Demonstrates parsing a JSON rule definition and running it against sample
/// key-value data. Illustrative only; not wired into the request pipeline.
/// </summary>
public static class RuleSample
{
    private const string SampleJson = """
    {
      "rules": [
        {
          "name": "route-premium",
          "if": { "field": "x-user-tier", "match": "equals", "value": "premium" },
          "then": { "backend-pool": "premium-pool", "S7PPriorityKey": "1" },
          "else": { "backend-pool": "standard-pool" }
        },
        {
          "name": "block-legacy-api",
          "if": { "field": "path", "match": "startsWith", "value": "/v0/" },
          "then": { "action": "deny" }
        },
        {
          "name": "large-region-match",
          "if": { "field": "x-region", "match": "regex", "value": "^(us|eu)-.*", "ignoreCase": true },
          "then": { "backend-pool": "regional-pool" },
          "else": { "backend-pool": "global-pool" }
        }
      ]
    }
    """;

    /// <summary>
    /// Parses the sample rules, evaluates them against sample data, and returns
    /// the results as human-readable lines.
    /// </summary>
    public static IReadOnlyList<string> Run()
    {
        // 1. Parse the JSON into an in-memory rule configuration.
        RuleConfig config = RuleConfigParser.Parse(SampleJson);

        // 2. Build a processor for the parsed rules.
        var processor = new RuleProcessor(config);

        // 3. Provide the sample data to evaluate against.
        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-user-tier"] = "premium",
            ["path"] = "/v1/chat/completions",
            ["method"] = "POST",
            ["x-region"] = "us-west-2"
        };

        // 4. Run the rules and collect the output.
        var output = new List<string>();

        foreach (var result in processor.Process(context))
        {
            output.Add($"Matched result: {Format(result)}");
        }

        var first = processor.ProcessFirst(context);
        output.Add($"First matched result: {(first is null ? "no-match" : Format(first))}");

        return output;
    }

    private static string Format(IReadOnlyDictionary<string, string> pairs)
        => string.Join(", ", pairs.Select(kv => $"{kv.Key}={kv.Value}"));
}
