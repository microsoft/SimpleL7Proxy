using SimpleL7Proxy.Rules;
using System.Text.Json;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class RuleTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["first-match-routing"] = new("Rules & Configuration", "First-match routing", "Ensures a request follows the first applicable branch and uses the configured fallback when no condition matches."),
            ["numeric-threshold-routing"] = new("Rules & Configuration", "Numeric threshold routing", "Prevents percentage and threshold rules from sending traffic down the wrong route."),
            ["string-matching"] = new("Rules & Configuration", "String matching", "Keeps header- and metadata-based routing decisions consistent across casing and positive or negative operators."),
            ["missing-data"] = new("Rules & Configuration", "Missing-data behavior", "Ensures incomplete request metadata follows a defined safe path instead of producing accidental matches."),
            ["range-boundaries"] = new("Rules & Configuration", "Range boundary routing", "Ensures rollout percentages and numeric bands include or exclude boundary values exactly as configured."),
            ["traffic-bucketing"] = new("Rules & Configuration", "Deterministic traffic bucketing", "Keeps the same request identity in a stable percentage bucket so gradual rollouts do not shift unpredictably."),
            ["rule-traceability"] = new("Rules & Configuration", "Rule composition and traceability", "Ensures complex rule trees select the intended branch and report a traceable path explaining the decision."),
            ["multi-rule-evaluation"] = new("Rules & Configuration", "Multi-rule evaluation", "Ensures all applicable routing rules are evaluated in a predictable order without losing valid results."),
            ["pattern-matching"] = new("Rules & Configuration", "Pattern matching", "Ensures pattern-based routing matches intended values consistently and rejects invalid patterns during configuration."),
            ["configuration-validation"] = new("Rules & Configuration", "Rule configuration validation", "Rejects unsafe or malformed rule configuration before it can affect live request routing."),
            ["rule-concurrency"] = new("Reliability & Capacity", "Rule engine concurrency", "Confirms rule evaluation remains stable when many requests are evaluated concurrently.")
        };

    private const string Json = """
    [
      {
        "name": "premium-tier",
                "if": { "name": "is-premium", "field": "x-user-tier", "match": "equals", "value": "premium", "ignoreCase": true },
                "then": { "name": "premium-route", "set": { "backend-pool": "premium-pool", "S7PPriorityKey": "1" } },
                "else": { "name": "standard-route", "set": { "backend-pool": "standard-pool" } }
      },
      {
        "name": "eu-region",
                "if": { "name": "is-eu", "field": "x-region", "match": "startsWith", "value": "eu-" },
                "then": { "name": "eu-residency", "set": { "data-residency": "eu" } }
      },
      {
        "name": "internal-agent",
                "if": { "name": "is-probe", "field": "user-agent", "match": "regex", "value": "^probe/.*", "ignoreCase": true },
                "then": { "name": "skip-auth", "set": { "action": "skip-auth" } }
      }
    ]
    """;

    private static RuleProcessor CreateProcessor()
        => new(RuleConfigParser.ParseRules(Json));

    [TestMethod]
    [RegressionTestCase("first-match-routing", "Matching condition selects the configured branch", "A matching request must receive the headers and priority values from the rule's then branch.")]
    public void ProcessFirst_ConditionTrue_ReturnsThenBranch()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string>
        {
            ["x-user-tier"] = "PREMIUM", // case-insensitive match
            ["x-region"] = "us-west-2"
        };

        var result = processor.ProcessFirst(context);

        Assert.IsNotNull(result);
        Assert.AreEqual("premium-pool", result["backend-pool"]);
        Assert.AreEqual("1", result["S7PPriorityKey"]);
    }

    [TestMethod]
    [RegressionTestCase("first-match-routing", "Non-matching condition selects the fallback branch", "A request that misses the primary condition must receive the rule's configured else result.")]
    public void ProcessFirst_ConditionFalse_ReturnsElseBranch()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string> { ["x-user-tier"] = "free" };

        var result = processor.ProcessFirst(context);

        Assert.IsNotNull(result);
        Assert.AreEqual("standard-pool", result["backend-pool"]);
    }

    [DataTestMethod]
    [RegressionTestCase(
        "numeric-threshold-routing",
        "Value {1} using {0} against {2} selects the {3} route",
        "Compares actual value {1} with threshold {2} using {0}; the expected route is {3}.")]
    [DataRow("greaterThan", "10", "9", "green")]
    [DataRow("greaterThan", "10", "10", "blue")]
    [DataRow("greaterThanOrEqual", "10", "10", "green")]
    [DataRow("greaterThanOrEqual", "9.99", "10", "blue")]
    [DataRow("lessThan", "10", "11", "green")]
    [DataRow("lessThan", "10", "10", "blue")]
    [DataRow("lessThanOrEqual", "10", "10", "green")]
    [DataRow("lessThanOrEqual", "10.01", "10", "blue")]
    public void ProcessFirst_NumericComparison_ReturnsExpectedBranch(
        string match,
        string actual,
        string threshold,
        string expectedPath)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            $$"""
            [{
                            "name": "numeric-comparison",
                            "if": { "name": "compare-value", "field": "traffic-percent", "match": "{{match}}", "value": "{{threshold}}" },
                            "then": { "name": "green-path", "set": { "path": "green" } },
                            "else": { "name": "blue-path", "set": { "path": "blue" } }
            }]
            """));
        var context = new Dictionary<string, string> { ["traffic-percent"] = actual };

        var result = processor.ProcessFirst(context);

        Assert.IsNotNull(result);
        Assert.AreEqual(expectedPath, result["path"]);
    }

    [DataTestMethod]
    [RegressionTestCase("numeric-threshold-routing", "Invalid numeric input uses the safe fallback", "Non-numeric operands or thresholds must not match a numeric rule and must select its else branch.")]
    [DataRow("greaterThan", "not-a-number", "10")]
    [DataRow("lessThan", "10", "not-a-number")]
    public void ProcessFirst_NumericComparison_InvalidOperand_ReturnsElseBranch(
        string match,
        string actual,
        string threshold)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            $$"""
            [{
                            "name": "invalid-numeric-comparison",
                            "if": { "name": "compare-value", "field": "traffic-percent", "match": "{{match}}", "value": "{{threshold}}" },
                            "then": { "name": "green-path", "set": { "path": "green" } },
                            "else": { "name": "blue-path", "set": { "path": "blue" } }
            }]
            """));
        var context = new Dictionary<string, string> { ["traffic-percent"] = actual };

        var result = processor.ProcessFirst(context);

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [TestMethod]
    [RegressionTestCase("missing-data", "Missing numeric fields use the fallback route", "A numeric rule whose source field is absent must not match and must select the configured else branch.")]
    public void ProcessFirst_NumericComparison_MissingField_ReturnsElseBranch()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "missing-numeric", "if": { "name": "compare-value", "field": "traffic-percent", "match": "lessThanOrEqual", "value": "10" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [DataTestMethod]
    [RegressionTestCase("string-matching", "String operators honor case-sensitivity settings", "Equals, contains, prefix, suffix, and negated operators must produce the configured result for case-sensitive and case-insensitive rules.")]
    [DataRow("equals", "Alpha", "alpha", true, true)]
    [DataRow("equals", "Alpha", "alpha", false, false)]
    [DataRow("notEquals", "Alpha", "alpha", false, true)]
    [DataRow("contains", "AlphaBeta", "PHAB", true, true)]
    [DataRow("notContains", "AlphaBeta", "gamma", true, true)]
    [DataRow("startsWith", "AlphaBeta", "alpha", true, true)]
    [DataRow("endsWith", "AlphaBeta", "BETA", true, true)]
    public void RuleCondition_StringOperators_RespectCaseMode(
        string match,
        string actual,
        string expected,
        bool ignoreCase,
        bool expectedResult)
    {
        var condition = new RuleCondition
        {
            Field = "value",
            Match = Enum.Parse<MatchOperator>(match, ignoreCase: true),
            Value = expected,
            IgnoreCase = ignoreCase
        };

        var result = condition.Evaluate(new Dictionary<string, string> { ["value"] = actual });

        Assert.AreEqual(expectedResult, result);
    }

    [DataTestMethod]
    [RegressionTestCase("missing-data", "Missing strings follow defined negation behavior", "Positive string and numeric operators must fail on missing fields while negated operators use their documented safe result.")]
    [DataRow("equals", false)]
    [DataRow("notEquals", true)]
    [DataRow("contains", false)]
    [DataRow("notContains", true)]
    [DataRow("startsWith", false)]
    [DataRow("endsWith", false)]
    [DataRow("greaterThan", false)]
    [DataRow("between", false)]
    public void RuleCondition_MissingField_UsesDefinedNegationSemantics(string match, bool expectedResult)
    {
        var condition = new RuleCondition
        {
            Field = "missing",
            Match = Enum.Parse<MatchOperator>(match, ignoreCase: true),
            Value = "10",
            Value2 = "20"
        };

        Assert.AreEqual(expectedResult, condition.Evaluate(new Dictionary<string, string>()));
    }

    [DataTestMethod]
    [RegressionTestCase("range-boundaries", "Configured range mode controls boundary membership", "Open, closed, and half-open ranges must include and exclude lower and upper boundary values exactly as configured.")]
    [DataRow("inOpenClosedRange", "10", "outside")]
    [DataRow("inOpenClosedRange", "20", "inside")]
    [DataRow("inClosedOpenRange", "10", "inside")]
    [DataRow("inClosedOpenRange", "20", "outside")]
    [DataRow("inOpenRange", "10", "outside")]
    [DataRow("inOpenRange", "15", "inside")]
    [DataRow("inOpenRange", "20", "outside")]
    [DataRow("inClosedRange", "10", "inside")]
    [DataRow("inClosedRange", "20", "inside")]
    public void ProcessFirst_BetweenMode_AppliesConfiguredBoundaries(
        string mode,
        string actual,
        string expectedPath)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            $$"""
            [{
                            "name": "range-check",
                            "if": { "name": "bucket-range", "field": "bucket", "match": "between", "value": "10", "value2": "20", "mode": "{{mode}}" },
                            "then": { "name": "inside-range", "set": { "path": "inside" } },
                            "else": { "name": "outside-range", "set": { "path": "outside" } }
            }]
            """));

        var result = processor.ProcessFirst(new Dictionary<string, string> { ["bucket"] = actual });

        Assert.IsNotNull(result);
        Assert.AreEqual(expectedPath, result["path"]);
    }

    [TestMethod]
    [RegressionTestCase("range-boundaries", "Ranges default to closed boundaries", "A between rule without an explicit mode must include both configured endpoints.")]
    public void ProcessFirst_BetweenWithoutMode_DefaultsToClosedRange()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "closed-range", "if": { "name": "bucket-range", "field": "bucket", "match": "between", "value": "10", "value2": "20" }, "then": { "name": "inside-range", "set": { "path": "inside" } }, "else": { "name": "outside-range", "set": { "path": "outside" } } }]"""));

        var lowerResult = processor.ProcessFirst(new Dictionary<string, string> { ["bucket"] = "10" });
        var upperResult = processor.ProcessFirst(new Dictionary<string, string> { ["bucket"] = "20" });

        Assert.IsNotNull(lowerResult);
        Assert.IsNotNull(upperResult);
        Assert.AreEqual("inside", lowerResult["path"]);
        Assert.AreEqual("inside", upperResult["path"]);
    }

    [DataTestMethod]
    [RegressionTestCase("range-boundaries", "Invalid ranges never match", "Malformed operands, malformed bounds, and reversed bounds must fail safely instead of routing a request into the range.")]
    [DataRow("not-a-number", "10", "20")]
    [DataRow("15", "not-a-number", "20")]
    [DataRow("15", "10", "not-a-number")]
    [DataRow("15", "20", "10")]
    public void RuleCondition_Between_InvalidOperandOrBounds_ReturnsFalse(
        string actual,
        string lower,
        string upper)
    {
        var condition = new RuleCondition
        {
            Field = "bucket",
            Match = MatchOperator.Between,
            Value = lower,
            Value2 = upper,
            Mode = RangeMode.InClosedRange
        };

        var result = condition.Evaluate(new Dictionary<string, string> { ["bucket"] = actual });

        Assert.IsFalse(result);
    }

    [DataTestMethod]
    [RegressionTestCase("range-boundaries", "Equal bounds match only a closed range", "When both bounds are equal, only the fully closed range mode may contain that value.")]
    [DataRow("inOpenClosedRange", false)]
    [DataRow("inClosedOpenRange", false)]
    [DataRow("inOpenRange", false)]
    [DataRow("inClosedRange", true)]
    public void RuleCondition_Between_EqualBoundsMatchOnlyClosedRange(string mode, bool expectedResult)
    {
        var condition = new RuleCondition
        {
            Field = "bucket",
            Match = MatchOperator.Between,
            Value = "10",
            Value2 = "10",
            Mode = Enum.Parse<RangeMode>(mode, ignoreCase: true)
        };

        var result = condition.Evaluate(new Dictionary<string, string> { ["bucket"] = "10" });

        Assert.AreEqual(expectedResult, result);
    }

    [DataTestMethod]
    [RegressionTestCase("range-boundaries", "Adjacent percentage bands select one route", "A chain of half-open ranges must assign boundary values to one and only one rollout cohort.")]
    [DataRow(0, "blue-1", "cohort/hash-0-25/blue-0-25")]
    [DataRow(25, "blue-2", "cohort/hash-25-50-clause/hash-25-50/blue-25-50")]
    [DataRow(50, "green-1", "cohort/hash-50-75-clause/hash-50-75/green-50-75")]
    [DataRow(75, "green-2", "cohort/hash-75-100-clause/hash-75-100/green-75-100")]
    [DataRow(100, "fallback", "cohort/fallback")]
    public void Process_BetweenWithElseIf_SelectsFirstClosedOpenRange(
        int bucket,
        string expectedPath,
        string expectedRuleName)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """
            [{
              "name": "cohort",
                            "if": { "name": "hash-0-25", "field": "S7PHash", "match": "between", "value": "0", "value2": "25", "mode": "inClosedOpenRange" },
                            "then": { "name": "blue-0-25", "set": { "path": "blue-1" } },
              "elseif": [
                                { "name": "hash-25-50-clause", "if": { "name": "hash-25-50", "field": "S7PHash", "match": "between", "value": "25", "value2": "50", "mode": "inClosedOpenRange" }, "then": { "name": "blue-25-50", "set": { "path": "blue-2" } } },
                                { "name": "hash-50-75-clause", "if": { "name": "hash-50-75", "field": "S7PHash", "match": "between", "value": "50", "value2": "75", "mode": "inClosedOpenRange" }, "then": { "name": "green-50-75", "set": { "path": "green-1" } } },
                                { "name": "hash-75-100-clause", "if": { "name": "hash-75-100", "field": "S7PHash", "match": "between", "value": "75", "value2": "100", "mode": "inClosedOpenRange" }, "then": { "name": "green-75-100", "set": { "path": "green-2" } } }
              ],
                            "else": { "name": "fallback", "set": { "path": "fallback" } }
            }]
            """));
        var matchedRuleNames = new List<string>();

        var result = processor.Process(
            new Dictionary<string, string>(),
            (short)bucket,
            matchedRuleNames).Single();

        Assert.AreEqual(expectedPath, result["path"]);
        CollectionAssert.AreEqual(new[] { expectedRuleName }, matchedRuleNames);
    }

    [DataTestMethod]
    [RegressionTestCase("traffic-bucketing", "S7P hash values select stable routes", "Known S7P hash values must remain in their expected rollout path so existing traffic assignments do not move.")]
    [DataRow(9, "green")]
    [DataRow(10, "blue")]
    public void Process_S7PHash_UsesPassedValue(int s7PHash, string expectedPath)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "hash-threshold", "if": { "name": "below-ten", "field": "S7PHash", "match": "lessThan", "value": "10" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));
        var context = new Dictionary<string, string> { ["S7PHash"] = "99" };

        var result = processor.Process(context, (short)s7PHash).Single();
        var firstResult = processor.ProcessFirst(context, (short)s7PHash);

        Assert.AreEqual(expectedPath, result["path"]);
        Assert.IsNotNull(firstResult);
        Assert.AreEqual(expectedPath, firstResult["path"]);
    }

    [DataTestMethod]
    [RegressionTestCase("traffic-bucketing", "Known identities produce stable FNV buckets", "Known input values must continue mapping to the same percentage buckets across releases.")]
    [DataRow("", 61)]
    [DataRow("a", 20)]
    [DataRow("b", 77)]
    [DataRow("c", 58)]
    public void RuleHash_CalculateBucket_ReturnsKnownFnvBucket(string value, int expectedBucket)
    {
        var bucket = RuleHash.CalculateBucket(value.AsSpan());

        Assert.AreEqual((short)expectedBucket, bucket);
    }

    [TestMethod]
    [RegressionTestCase("traffic-bucketing", "Multiple hash values use an unambiguous separator", "Combining identity values must not create collisions caused by ambiguous concatenation.")]
    public void RuleHash_CalculateBucket_TwoValuesUsesSeparator()
    {
        var bucket = RuleHash.CalculateBucket("a".AsSpan(), "b".AsSpan());

        Assert.AreEqual((short)8, bucket);
        Assert.AreEqual(RuleHash.CalculateBucket("a\nb".AsSpan()), bucket);
        Assert.AreNotEqual(RuleHash.CalculateBucket("ab".AsSpan()), bucket);
    }

    [TestMethod]
    [RegressionTestCase("traffic-bucketing", "Named request fields can drive stable bucketing", "A Hash: field rule must bucket the named request value and select the corresponding rollout path.")]
    public void Process_HashField_HashesNamedContextValue()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "user-hash", "if": { "name": "hash-is-twenty", "field": "Hash:UserID", "match": "equals", "value": "20" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));
        var context = new Dictionary<string, string>
        {
            ["UserID"] = "a",
            ["Hash:UserID"] = "not-the-computed-hash"
        };

        var result = processor.Process(context).Single();
        var firstResult = processor.ProcessFirst(context);

        Assert.AreEqual("green", result["path"]);
        Assert.IsNotNull(firstResult);
        Assert.AreEqual("green", firstResult["path"]);
    }

    [TestMethod]
    [RegressionTestCase("traffic-bucketing", "Hash:S7PHash uses the supplied request bucket", "Rules referencing Hash:S7PHash must use the proxy's existing request hash instead of hashing its textual value again.")]
    public void Process_HashS7PHash_UsesPassedValueInsteadOfContext()
    {
        var expectedBucket = RuleHash.CalculateBucket("9".AsSpan());
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            $$"""[{ "name": "hash-s7p", "if": { "name": "expected-hash", "field": "Hash:S7PHash", "match": "equals", "value": "{{expectedBucket}}" }, "then": { "name": "matched", "set": { "path": "green" } }, "else": { "name": "fallback", "set": { "path": "blue" } } }]"""));
        var context = new Dictionary<string, string> { ["S7PHash"] = "99" };

        var result = processor.ProcessFirst(context, s7PHash: 9);

        Assert.IsNotNull(result);
        Assert.AreEqual("green", result["path"]);
    }

    [TestMethod]
    [RegressionTestCase("traffic-bucketing", "Every hash remains inside the percentage range", "All calculated buckets must stay between zero and ninety-nine so rollout rules always receive a valid percentage.")]
    public void RuleHash_CalculateBucket_AlwaysReturnsPercentageBucket()
    {
        for (var index = 0; index < 10_000; index++)
        {
            var bucket = RuleHash.CalculateBucket(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsTrue(bucket is >= 0 and <= 99, $"Input {index} produced bucket {bucket}.");
        }
    }

    [TestMethod]
    [RegressionTestCase("rule-traceability", "Applied rule paths report actual branch decisions", "Decision telemetry must list only branches that changed the request, including explicit fallback branches.")]
    public void Process_MatchedRuleNames_ReportsAppliedThenAndElseBranchesOnly()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """
            [
                            { "name": "then-rule", "if": { "name": "premium-tier", "field": "tier", "match": "equals", "value": "premium" }, "then": { "name": "then-output", "set": { "then": "applied" } } },
                            { "name": "else-rule", "if": { "name": "eu-region", "field": "region", "match": "equals", "value": "eu" }, "then": { "name": "eu-output", "set": { "else": "not-applied" } }, "else": { "name": "else-output", "set": { "else": "applied" } } },
                            { "name": "no-branch-rule", "if": { "name": "missing-value", "field": "missing", "match": "equals", "value": "yes" }, "then": { "name": "missing-output", "set": { "missing": "not-applied" } } },
                            { "name": "mode-rule", "if": { "name": "other-mode", "field": "mode", "match": "equals", "value": "other" }, "then": { "name": "other-output", "set": { "mode": "not-applied" } }, "else": { "name": "current-output", "set": { "mode": "applied" } } }
            ]
            """));
        var context = new Dictionary<string, string>
        {
            ["tier"] = "premium",
            ["region"] = "us",
            ["mode"] = "current"
        };
        var matchedRuleNames = new List<string>();

        var results = processor.Process(context, matchedRuleNames: matchedRuleNames).ToList();

        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("applied", results[0]["then"]);
        Assert.AreEqual("applied", results[1]["else"]);
        Assert.AreEqual("applied", results[2]["mode"]);
        CollectionAssert.AreEqual(
            new[]
            {
                "then-rule/premium-tier/then-output",
                "else-rule/else-output",
                "mode-rule/current-output"
            },
            matchedRuleNames);
    }

    [DataTestMethod]
    [RegressionTestCase("rule-traceability", "Nested rules expose the selected value and full decision path", "Nested and elseif branches must apply the intended result and expose the complete named path used to reach it.")]
    [DataRow("direct", "basic", "us", "direct", "outer/direct-channel/direct-set")]
    [DataRow("indirect", "premium", "eu", "premium-eu", "outer/secondary-rule/premium-tier/region-rule/eu-region/premium-eu-set")]
    [DataRow("indirect", "premium", "us", "premium-global", "outer/secondary-rule/premium-tier/region-rule/premium-global-set")]
    [DataRow("indirect", "basic", "eu", "standard", "outer/secondary-rule/standard-set")]
    public void Process_NestedRules_ReturnsSetAndFullNamedPath(
        string channel,
        string tier,
        string region,
        string expectedRoute,
        string expectedPath)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """
            [{
                "name": "outer",
                "if": { "name": "direct-channel", "field": "channel", "match": "equals", "value": "direct" },
                "then": { "name": "direct-set", "set": { "route": "direct" } },
                "else": {
                    "name": "secondary-rule",
                    "if": { "name": "premium-tier", "field": "tier", "match": "equals", "value": "premium" },
                    "then": {
                        "name": "region-rule",
                        "if": { "name": "eu-region", "field": "region", "match": "equals", "value": "eu" },
                        "then": { "name": "premium-eu-set", "set": { "route": "premium-eu" } },
                        "else": { "name": "premium-global-set", "set": { "route": "premium-global" } }
                    },
                    "else": { "name": "standard-set", "set": { "route": "standard" } }
                }
            }]
            """));
        var context = new Dictionary<string, string>
        {
            ["channel"] = channel,
            ["tier"] = tier,
            ["region"] = region
        };
        var matchedRuleNames = new List<string>();

        var result = processor.Process(context, matchedRuleNames: matchedRuleNames).Single();

        Assert.AreEqual(expectedRoute, result["route"]);
        CollectionAssert.AreEqual(new[] { expectedPath }, matchedRuleNames);
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Rule nesting enforces the supported depth limit", "Configuration at the maximum supported nesting depth must compile while one additional level is rejected.")]
    public void RuleConfig_Compile_AllowsMaximumDepthAndRejectsNextLevel()
    {
        static RuleConfig CreateConfig(int nestedConditionCount)
        {
            RuleNode branch = new()
            {
                Name = "leaf",
                Set = new Dictionary<string, string> { ["result"] = "matched" }
            };

            for (var index = 0; index < nestedConditionCount; index++)
            {
                branch = new RuleNode
                {
                    Name = $"nested-{index}",
                    If = new RuleCondition
                    {
                        Name = $"condition-{index}",
                        Field = "value",
                        Match = MatchOperator.Equals,
                        Value = "yes"
                    },
                    Then = branch
                };
            }

            return new RuleConfig
            {
                Rules =
                [
                    new Rule
                    {
                        Name = "root",
                        If = new RuleCondition
                        {
                            Name = "root-condition",
                            Field = "value",
                            Match = MatchOperator.Equals,
                            Value = "yes"
                        },
                        Then = branch
                    }
                ]
            };
        }

        CreateConfig(nestedConditionCount: 15).Compile();
        var exception = Assert.ThrowsException<ArgumentException>(
            () => CreateConfig(nestedConditionCount: 16).Compile());

        StringAssert.Contains(exception.Message, "cannot exceed 16 levels");
    }

    [TestMethod]
    [RegressionTestCase("missing-data", "Missing hash sources select the fallback branch", "A Hash: rule without its source value must not create a bucket or accidentally match the primary route.")]
    public void ProcessFirst_HashField_MissingSource_ReturnsElseBranch()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "missing-hash", "if": { "name": "below-ten", "field": "Hash:Missing", "match": "lessThan", "value": "10" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [TestMethod]
    [RegressionTestCase("first-match-routing", "No matching rule returns the configured default", "When no condition applies, first-match processing must return its documented default outcome.")]
    public void ProcessFirst_NoMatch_ReturnsDefault()
    {
        // Use rules where every branch can be null: a single rule with no else.
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "no-match", "if": { "name": "x-is-y", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "matched-output", "set": { "k": "v" } } }]"""));
        var context = new Dictionary<string, string> { ["x"] = "not-y" };

        var fallback = new Dictionary<string, string> { ["backend-pool"] = "default" };
    var result = processor.ProcessFirst(context, defaultResult: fallback);

        Assert.AreSame(fallback, result);
    }

    [TestMethod]
    [RegressionTestCase("multi-rule-evaluation", "All matching rules are returned in order", "Multi-rule processing must retain every applicable result in configuration order.")]
    public void Process_ReturnsAllMatchingResultsInOrder()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string>
        {
            ["x-user-tier"] = "premium",
            ["x-region"] = "eu-central-1",
            ["user-agent"] = "probe/1.0"
        };

        var results = processor.Process(context).ToList();

        // premium (then) + eu (then) + internal-agent (then) all match.
        Assert.AreEqual(3, results.Count);
        Assert.AreEqual("premium-pool", results[0]["backend-pool"]);
        Assert.AreEqual("eu", results[1]["data-residency"]);
        Assert.AreEqual("skip-auth", results[2]["action"]);
    }

    [TestMethod]
    [RegressionTestCase("missing-data", "Rules without a fallback skip missing fields", "A rule whose source field is absent and has no else branch must be skipped without producing output.")]
    public void Process_MissingField_SkipsRuleWithoutElse()
    {
        var processor = CreateProcessor();
        // x-region and user-agent absent -> those rules have no else, so are skipped.
        var context = new Dictionary<string, string> { ["x-user-tier"] = "premium" };

        var results = processor.Process(context).ToList();

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("premium-pool", results[0]["backend-pool"]);
    }

    [TestMethod]
    [RegressionTestCase("pattern-matching", "Regex matching honors case-insensitive rules", "Pattern-based routing must match equivalent user-agent values regardless of letter casing.")]
    public void Regex_Matches_CaseInsensitive()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string> { ["user-agent"] = "PROBE/2.5" };

        var results = processor.Process(context).ToList();

        Assert.IsTrue(results.Any(r => r.TryGetValue("action", out var a) && a == "skip-auth"));
    }

    [TestMethod]
    [RegressionTestCase("pattern-matching", "Invalid regex is rejected during configuration", "A malformed regular expression must fail at parse time rather than breaking request processing later.")]
    public void ParseRules_InvalidRegex_ThrowsAtParseTime()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            RuleConfigParser.ParseRules(
                """[{ "name": "invalid-regex", "if": { "name": "bad-pattern", "field": "x", "match": "regex", "value": "(" }, "then": { "name": "output", "set": {} } }]"""));
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Invalid JSON configuration is rejected", "Rule configuration that is not valid JSON must fail with a clear argument error before activation.")]
    public void ParseRules_InvalidJson_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => RuleConfigParser.ParseRules("not json"));
    }

    [DataTestMethod]
    [RegressionTestCase("configuration-validation", "Invalid rule structures are rejected with useful errors", "Missing names, conflicting node types, incomplete branches, and malformed elseif clauses must be rejected before routing begins.")]
    [DataRow(
        """[{ "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": { "k": "v" } } }]""",
        "Every rule node must have a name")]
    [DataRow(
        """[{ "name": "rule", "if": { "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": { "k": "v" } } }]""",
        "must give its if condition a name")]
    [DataRow(
        """[{ "name": "both", "set": {}, "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": {} } }]""",
        "exactly one of set or if")]
    [DataRow(
        """[{ "name": "empty" }]""",
        "exactly one of set or if")]
    [DataRow(
        """[{ "name": "leaf", "set": {}, "else": { "name": "fallback", "set": {} } }]""",
        "cannot define then, elseif, or else")]
    [DataRow(
        """[{ "name": "rule", "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" } }]""",
        "must define a then branch")]
    [DataRow(
        """[{ "name": "rule", "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": {} }, "elseif": [{ "if": { "name": "alternate", "field": "x", "match": "equals", "value": "z" }, "then": { "name": "alternate-output", "set": {} } }] }]""",
        "Every elseif clause must have a name")]
    [DataRow(
        """[{ "name": "rule", "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": {} }, "elseif": [{ "name": "alternate-clause", "if": { "field": "x", "match": "equals", "value": "z" }, "then": { "name": "alternate-output", "set": {} } }] }]""",
        "must give its if condition a name")]
    [DataRow(
        """[{ "name": "rule", "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": {} }, "elseif": [{ "name": "alternate-clause", "if": { "name": "alternate", "field": "x", "match": "equals", "value": "z" } }] }]""",
        "must define a then branch")]
    public void ParseRules_InvalidSchema_ThrowsArgumentException(string json, string expectedMessage)
    {
        var exception = Assert.ThrowsException<ArgumentException>(() => RuleConfigParser.ParseRules(json));

        StringAssert.Contains(exception.Message, expectedMessage);
    }

    [DataTestMethod]
    [RegressionTestCase("configuration-validation", "Unknown match operators are rejected", "String and numeric values outside the supported match-operator enum must not be accepted as rule configuration.")]
    [DataRow("\"unknown\"")]
    [DataRow("1")]
    public void ParseRules_InvalidMatchEnum_ThrowsArgumentException(string matchJson)
    {
        var json = $$"""[{ "name": "rule", "if": { "name": "condition", "field": "x", "match": {{matchJson}}, "value": "y" }, "then": { "name": "output", "set": {} } }]""";

        Assert.ThrowsException<ArgumentException>(() => RuleConfigParser.ParseRules(json));
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Parse and TryParse report success and failure consistently", "Wrapped rule documents must parse successfully while TryParse returns a safe empty result and error for invalid input.")]
    public void ParseAndTryParse_WrappedRules_ReportSuccessAndFailure()
    {
        const string json =
            """{ "rules": [{ "name": "rule", "if": { "name": "condition", "field": "x", "match": "equals", "value": "y" }, "then": { "name": "output", "set": { "result": "matched" } } }] }""";

        var parsed = RuleConfigParser.Parse(json);
        var tryParseSucceeded = RuleConfigParser.TryParse(json, out var tryParsed, out var successError);
        var tryParseFailed = RuleConfigParser.TryParse("not json", out var failedConfig, out var failureError);

        Assert.AreEqual(1, parsed.Rules.Count);
        Assert.IsTrue(tryParseSucceeded);
        Assert.AreEqual(1, tryParsed.Rules.Count);
        Assert.IsNull(successError);
        Assert.IsFalse(tryParseFailed);
        Assert.AreEqual(0, failedConfig.Rules.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failureError));
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Rule files allow comments and trailing commas", "Operational rule files may use documented JSON conveniences without changing their routing behavior.")]
    public void Parse_WrappedRules_AllowsCommentsAndTrailingCommas()
    {
        const string json =
            """
            {
                // Profile rules may include comments.
                "rules": [
                    {
                        "name": "rule",
                        "if": { "name": "condition", "field": "x", "match": "equals", "value": "y", },
                        "then": { "name": "output", "set": { "result": "matched", }, },
                    },
                ],
            }
            """;

        var processor = new RuleProcessor(RuleConfigParser.Parse(json));
        var result = processor.ProcessFirst(new Dictionary<string, string> { ["x"] = "y" });

        Assert.IsNotNull(result);
        Assert.AreEqual("matched", result["result"]);
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Bundled rule sample produces expected routes", "The documented sample configuration must remain executable and produce its expected backend selections.")]
    public void RuleSample_Run_ParsesWrappedConfigAndReturnsExpectedResults()
    {
        var output = RuleSample.Run();

        Assert.AreEqual(3, output.Count);
        StringAssert.Contains(output[0], "backend-pool=premium-pool");
        StringAssert.Contains(output[1], "backend-pool=regional-pool");
        StringAssert.Contains(output[2], "backend-pool=premium-pool");
    }

    [TestMethod]
    [RegressionTestCase("configuration-validation", "Every shipped profile rule set parses", "All rule sets in the deployed profile configuration must compile before the configuration is considered releasable.")]
    public void ConfigJson_AllProfileRules_ParseSuccessfully()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "configs", "profile-config.json");
        Assert.IsTrue(File.Exists(configPath), $"Missing profile config: {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var parsedRuleSets = 0;

        foreach (var profile in document.RootElement.EnumerateArray())
        {
            if (profile.TryGetProperty("rules", out var rules))
            {
                var config = RuleConfigParser.ParseRules(rules.GetRawText());
                Assert.IsTrue(config.Rules.Count > 0);
                parsedRuleSets++;
            }
        }

        Assert.IsTrue(parsedRuleSets > 0, "At least one profile must define rules.");
    }

    [TestMethod]
    [RegressionTestCase("rule-concurrency", "Rule evaluation remains stable under 1,000 threads", "A realistic shared rule set must return the same route during ten seconds of concurrent evaluation without mismatches.")]
    public void Stress_ThousandThreads_TenSeconds_FromConfigFile()
    {
        const int threadCount = 1000;
        const int durationSeconds = 10;

        // Load the realistic rule set from a sample config file on disk.
        var configPath = Path.Combine(AppContext.BaseDirectory, "configs", "rules.sample.json");
        Assert.IsTrue(File.Exists(configPath), $"Missing sample config: {configPath}");

        var processor = new RuleProcessor(RuleConfigParser.ParseRules(File.ReadAllText(configPath)));

        // A realistic inference request: block-legacy-api (rule 1) is skipped, then
        // route-inference (rule 2) matches "/v1/chat/completions" -> chat-pool.
        const string expectedPool = "chat-pool";

        // startGate releases all threads at once; stopSignal tells them to exit after 10s.
        using var startGate = new ManualResetEventSlim(false);
        using var stopSignal = new ManualResetEventSlim(false);

        long totalCount = 0;
        long mismatches = 0;
        var ready = new CountdownEvent(threadCount);
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                // Each thread uses its own context instance (no shared mutable state).
                var context = new Dictionary<string, string>
                {
                    ["path"] = "/v1/chat/completions",
                    ["method"] = "POST",
                    ["x-user-tier"] = "enterprise",
                    ["x-region"] = "eu-west-1",
                    ["model"] = "gpt-4o",
                    ["authorization"] = "Bearer sk-ABCDEFGHIJ0123456789xyz",
                    ["user-agent"] = "Mozilla/5.0",
                    ["content-type"] = "application/json"
                };
                long local = 0;
                long localMismatch = 0;

                ready.Signal();       // announce this thread is primed
                startGate.Wait();     // block until all threads start together

                while (!stopSignal.IsSet)
                {
                    var result = processor.ProcessFirst(context);
                    if (result is null || !result.TryGetValue("backend-pool", out var pool) || pool != expectedPool)
                    {
                        localMismatch++;
                    }
                    local++;
                }

                Interlocked.Add(ref totalCount, local);
                Interlocked.Add(ref mismatches, localMismatch);
            })
            {
                IsBackground = true,
                Name = $"rule-stress-{t}"
            };
            threads[t].Start();
        }

        // Wait until every thread is primed, then start them all and time the run.
        ready.Wait();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        startGate.Set();
        Thread.Sleep(TimeSpan.FromSeconds(durationSeconds));
        stopSignal.Set();

        foreach (var thread in threads)
        {
            thread.Join();
        }
        sw.Stop();

        var perSecond = totalCount / sw.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"{threadCount} threads ran for {sw.Elapsed.TotalSeconds:F1}s: " +
            $"total evaluations = {totalCount:N0} ({perSecond:N0} eval/sec, mismatches = {mismatches})");

        Assert.AreEqual(0, mismatches, "Every evaluation must return the expected pool.");
        Assert.IsTrue(totalCount > 0, "Threads must have performed work.");
    }
}


