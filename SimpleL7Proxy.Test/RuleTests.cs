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

    [TestMethod]
    public void ProcessFirst_NoMatch_ReturnsDefault()
    {
        // Use rules where every branch can be null: a single rule with no else.
        var processor = new RuleProcessor(RuleConfigParser.ParseRules(
            """[{ "if": { "field": "x", "match": "equals", "value": "y" }, "then": { "k": "v" } }]"""));
        var context = new Dictionary<string, string> { ["x"] = "not-y" };

        var fallback = new Dictionary<string, string> { ["backend-pool"] = "default" };
        var result = processor.ProcessFirst(context, fallback);

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


