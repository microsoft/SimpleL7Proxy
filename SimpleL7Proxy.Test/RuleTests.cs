using SimpleL7Proxy.Rules;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class RuleTests
{
    private const string Json = """
    [
      {
        "name": "premium-tier",
        "if": { "field": "x-user-tier", "match": "equals", "value": "premium", "ignoreCase": true },
        "then": { "backend-pool": "premium-pool", "S7PPriorityKey": "1" },
        "else": { "backend-pool": "standard-pool" }
      },
      {
        "name": "eu-region",
        "if": { "field": "x-region", "match": "startsWith", "value": "eu-" },
        "then": { "data-residency": "eu" }
      },
      {
        "name": "internal-agent",
        "if": { "field": "user-agent", "match": "regex", "value": "^probe/.*", "ignoreCase": true },
        "then": { "action": "skip-auth" }
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
              "if": { "field": "traffic-percent", "match": "{{match}}", "value": "{{threshold}}" },
              "then": { "path": "green" },
              "else": { "path": "blue" }
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
              "if": { "field": "traffic-percent", "match": "{{match}}", "value": "{{threshold}}" },
              "then": { "path": "green" },
              "else": { "path": "blue" }
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
            """[{ "if": { "field": "traffic-percent", "match": "lessThanOrEqual", "value": "10" }, "then": { "path": "green" }, "else": { "path": "blue" } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [DataTestMethod]
    [DataRow(9, "green")]
    [DataRow(10, "blue")]
    public void Process_S7PHash_UsesPassedValue(int s7PHash, string expectedPath)
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "if": { "field": "S7PHash", "match": "lessThan", "value": "10" }, "then": { "path": "green" }, "else": { "path": "blue" } }]"""));
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
            """[{ "if": { "field": "Hash:UserID", "match": "equals", "value": "20" }, "then": { "path": "green" }, "else": { "path": "blue" } }]"""));
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
              { "name": "then-rule", "if": { "field": "tier", "match": "equals", "value": "premium" }, "then": { "then": "applied" } },
              { "name": "else-rule", "if": { "field": "region", "match": "equals", "value": "eu" }, "then": { "else": "not-applied" }, "else": { "else": "applied" } },
              { "name": "no-branch-rule", "if": { "field": "missing", "match": "equals", "value": "yes" }, "then": { "missing": "not-applied" } },
              { "if": { "field": "mode", "match": "equals", "value": "other" }, "then": { "unnamed": "not-applied" }, "else": { "unnamed": "applied" } }
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
        Assert.AreEqual("applied", results[2]["unnamed"]);
        CollectionAssert.AreEqual(new[] { "then-rule", "else-rule-else" }, matchedRuleNames);
    }

    [TestMethod]
    public void ProcessFirst_HashField_MissingSource_ReturnsElseBranch()
    {
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "if": { "field": "Hash:Missing", "match": "lessThan", "value": "10" }, "then": { "path": "green" }, "else": { "path": "blue" } }]"""));

        var result = processor.ProcessFirst(new Dictionary<string, string>());

        Assert.IsNotNull(result);
        Assert.AreEqual("blue", result["path"]);
    }

    [TestMethod]
    public void ProcessFirst_NoMatch_ReturnsDefault()
    {
        // Use rules where every branch can be null: a single rule with no else.
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "if": { "field": "x", "match": "equals", "value": "y" }, "then": { "k": "v" } }]"""));
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
                """[{ "if": { "field": "x", "match": "regex", "value": "(" }, "then": {} }]"""));
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


