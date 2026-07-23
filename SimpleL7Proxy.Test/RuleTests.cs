using SimpleL7Proxy.Rules;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class RuleTests
{
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
    public void ProcessFirst_ConditionFalse_ReturnsElseBranch()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string> { ["x-user-tier"] = "free" };

        var result = processor.ProcessFirst(context);

        Assert.IsNotNull(result);
        Assert.AreEqual("standard-pool", result["backend-pool"]);
    }

    [DataTestMethod]
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
    public void ProcessFirst_NumericComparison_MissingField_ReturnsElseBranch()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "missing-numeric", "if": { "name": "compare-value", "field": "traffic-percent", "match": "lessThanOrEqual", "value": "10" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [DataTestMethod]
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
    public void RuleHash_CalculateBucket_TwoValuesUsesSeparator()
    {
        var bucket = RuleHash.CalculateBucket("a".AsSpan(), "b".AsSpan());

        Assert.AreEqual((short)8, bucket);
        Assert.AreEqual(RuleHash.CalculateBucket("a\nb".AsSpan()), bucket);
        Assert.AreNotEqual(RuleHash.CalculateBucket("ab".AsSpan()), bucket);
    }

    [TestMethod]
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
    public void ProcessFirst_HashField_MissingSource_ReturnsElseBranch()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "name": "missing-hash", "if": { "name": "below-ten", "field": "Hash:Missing", "match": "lessThan", "value": "10" }, "then": { "name": "green-path", "set": { "path": "green" } }, "else": { "name": "blue-path", "set": { "path": "blue" } } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [TestMethod]
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
    public void Regex_Matches_CaseInsensitive()
    {
        var processor = CreateProcessor();
        var context = new Dictionary<string, string> { ["user-agent"] = "PROBE/2.5" };

        var results = processor.Process(context).ToList();

        Assert.IsTrue(results.Any(r => r.TryGetValue("action", out var a) && a == "skip-auth"));
    }

    [TestMethod]
    public void ParseRules_InvalidRegex_ThrowsAtParseTime()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            RuleConfigParser.ParseRules(
                """[{ "name": "invalid-regex", "if": { "name": "bad-pattern", "field": "x", "match": "regex", "value": "(" }, "then": { "name": "output", "set": {} } }]"""));
    }

    [TestMethod]
    public void ParseRules_InvalidJson_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(() => RuleConfigParser.ParseRules("not json"));
    }

    [TestMethod]
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


