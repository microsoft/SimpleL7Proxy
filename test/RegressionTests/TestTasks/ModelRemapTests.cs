using System.Text.Json;
using SharedParser.Tokenomics.LLM.Usage;
using SimpleL7Proxy.Tokenomics;
using SharedParser.Tokenomics.LLM.Models;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Tests for the LLM model/field remapper: <see cref="ModelMap"/> family
/// transitions and model field compatibility transformations.
/// </summary>
[TestClass]
public sealed class ModelRemapTests : IRegressionTestMetadata
{
    public IReadOnlyDictionary<string, RegressionFeature> RegressionFeatures { get; } =
        new Dictionary<string, RegressionFeature>
        {
            ["model-family-mapping"] = new(
                "AI Request Compatibility",
                "Model family mapping",
                "Prevents requests from carrying fields that are unsupported by the backend model family selected for routing."),
            ["model-detection"] = new(
                "AI Request Compatibility",
                "Model detection",
                "Ensures the proxy identifies the requested model without changing valid payloads or trusting nested lookalike fields."),
            ["model-override"] = new(
                "AI Request Compatibility",
                "Model override rewriting",
                "Keeps rerouted requests valid when the proxy switches a request to a different model family."),
            ["request-word-count"] = new(
                "AI Request Compatibility",
                "Request word counting",
                "Counts whitespace-delimited words in request string values without counting JSON property names or the routing model.")
        };

    // ---- ModelMap: family transition selection -------------------------

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "GPT-4o to GPT-5 uses reasoning transforms", "Selecting GPT-5 for a classic request must apply the field removal and rename rules required by reasoning models.")]
    public void ModelMap_ClassicToGpt5_UsesClassicToReasoningMaps()
    {
        var (remove, rename) = ModelMap.Get("gpt-4o", "gpt-5");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "Classic to reasoning uses reasoning transforms", "Routing a classic request to an o-series model must select the classic-to-reasoning compatibility maps.")]
    public void ModelMap_ClassicToReasoning_UsesClassicToReasoningMaps()
    {
        var (remove, rename) = ModelMap.Get("gpt-4.1", "o3");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "GPT-5 to classic removes unsupported fields", "Routing GPT-5 input to a classic model must remove GPT-5-only fields and restore classic token field names.")]
    public void ModelMap_Gpt5ToClassic_RemovesGpt5FieldsAndRenamesToClassic()
    {
        var (remove, rename) = ModelMap.Get("gpt-5-mini", "gpt-4o");

        Assert.AreSame(FieldRemovalMap.Gpt5ToClassic, remove);
        Assert.AreSame(FieldRenameMap.ReasoningToClassic, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "GPT-5 to reasoning removes verbosity only", "Routing between reasoning families must drop unsupported verbosity without renaming compatible token fields.")]
    public void ModelMap_Gpt5ToReasoning_RemovesVerbosityNoRename()
    {
        var (remove, rename) = ModelMap.Get("gpt-5", "o4-mini");

        Assert.AreSame(FieldRemovalMap.Gpt5ToReasoning, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "Reasoning to classic restores classic fields", "Routing an o-series request to a classic model must select the reasoning-to-classic compatibility maps.")]
    public void ModelMap_ReasoningToClassic_UsesReasoningToClassicMaps()
    {
        var (remove, rename) = ModelMap.Get("o3", "gpt-4");

        Assert.AreSame(FieldRemovalMap.ReasoningToClassic, remove);
        Assert.AreSame(FieldRenameMap.ReasoningToClassic, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "Same-family routing preserves request fields", "Switching models within the same family must not remove or rename otherwise valid request fields.")]
    public void ModelMap_SameFamily_NoTransform()
    {
        var (remove, rename) = ModelMap.Get("gpt-4", "gpt-4o");

        Assert.AreSame(FieldRemovalMap.Empty, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "Unknown source models remain untouched", "An unrecognized model must not trigger speculative field removal or renaming.")]
    public void ModelMap_UnknownModel_NoTransform()
    {
        var (remove, rename) = ModelMap.Get("llama-3", "gpt-4");

        Assert.AreSame(FieldRemovalMap.Empty, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "Model family matching ignores case", "Model routing must choose the same compatibility maps regardless of model-name casing.")]
    public void ModelMap_FamilyDetection_IsCaseInsensitive()
    {
        var (remove, rename) = ModelMap.Get("GPT-4O", "GPT-5");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    [TestMethod]
    [RegressionTestCase("model-family-mapping", "GPT-4o to GPT-6 Sol removes temperature", "Routing a classic request to GPT-6 Sol must omit temperature so the backend uses its supported default, while retaining the completion-token rename.")]
    public void ModelMap_ClassicToGpt6_RemovesTemperatureAndRenamesTokens()
    {
        var (remove, rename) = ModelMap.Get("gpt-4o", "gpt-6-sol");

        Assert.AreSame(FieldRemovalMap.ClassicToGpt6, remove);
        Assert.IsTrue(remove.Contains("temperature"));
        Assert.AreEqual("max_completion_tokens", rename["max_tokens"]);
    }

    [TestMethod]
    [RegressionTestCase("model-override", "GPT-6 rewrite preserves unrelated fields", "Only temperature is removed from the reported streaming request; nested settings and all other values remain intact.")]
    public void MergeModel_Gpt6_PreservesStreamingRequest()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""
            {
              "messages": [{ "content": "tell me a joke", "role": "user" }],
              "model": "gpt-4o",
              "temperature": 0.7,
              "top_p": 1,
              "max_completion_tokens": 1024,
              "frequency_penalty": 0,
              "presence_penalty": 0,
              "seed": 0,
              "stream": true,
              "stream_options": { "include_usage": true }
            }
            """);
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        var rewritten = SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", "gpt-6-sol", parsed);
        using var original = JsonDocument.Parse(body);
        using var actual = JsonDocument.Parse(rewritten);
        Assert.AreEqual("gpt-6-sol", actual.RootElement.GetProperty("model").GetString());
        Assert.IsFalse(actual.RootElement.TryGetProperty("temperature", out _));
        Assert.AreEqual(9, actual.RootElement.EnumerateObject().Count());
        foreach (var property in original.RootElement.EnumerateObject())
        {
            if (property.Name is "temperature" or "model")
                continue;
            Assert.AreEqual(property.Value.GetRawText(), actual.RootElement.GetProperty(property.Name).GetRawText());
        }
    }

    [TestMethod]
    [DataRow("{\"temperature\":0.7,\"model\":\"gpt-4o\",\"stream\":true}")]
    [DataRow("{\"model\":\"gpt-4o\",\"temperature\":0.7,\"stream\":true}")]
    [DataRow("{\"model\":\"gpt-4o\",\"stream\":true,\"temperature\":0.7}")]
    [DataRow("{\"model\":\"gpt-4o\",\"stream\":true,\"temperature\":{\"nested\":[1,{\"value\":\"}\"}]}}")]
    public void MergeModel_Removal_HandlesPropertyBoundaries(string json)
    {
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(System.Text.Encoding.UTF8.GetBytes(json));
        using var actual = JsonDocument.Parse(SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", "gpt-6-sol", parsed));
        Assert.AreEqual(2, actual.RootElement.EnumerateObject().Count());
        Assert.AreEqual("gpt-6-sol", actual.RootElement.GetProperty("model").GetString());
        Assert.IsTrue(actual.RootElement.GetProperty("stream").GetBoolean());
    }

    [TestMethod]
    public void MergeModel_Unchanged_ReturnsOriginalMemory()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{ \"model\":\"gpt-4o\", \"temperature\":0.7 }");
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        var actual = SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", "gpt-4o", parsed);
        Assert.AreEqual(parsed.OriginalBodyBytes, actual);
        Assert.AreEqual(0, parsed.TopLevelFields.Count);
        Assert.IsNull(parsed.ModelValueOffset);
    }

    [TestMethod]
    public void MergeModel_Rename_PreservesNestedLookalikes()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{"messages":[{"model":"gpt-4o","max_tokens":7}],"model":"gpt-4o","max_tokens":1024}""");
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        using var actual = JsonDocument.Parse(SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", "gpt-6-sol", parsed));
        Assert.AreEqual(1024, actual.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.IsFalse(actual.RootElement.TryGetProperty("max_tokens", out _));
        Assert.AreEqual("gpt-6-sol", actual.RootElement.GetProperty("model").GetString());
        Assert.AreEqual("gpt-4o", actual.RootElement.GetProperty("messages")[0].GetProperty("model").GetString());
        Assert.AreEqual(7, actual.RootElement.GetProperty("messages")[0].GetProperty("max_tokens").GetInt32());
    }

    [TestMethod]
    public void MergeModel_ConsecutiveRemovals_PreservesRetainedValues()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{"temperature":0.7,"top_p":1,"model":"gpt-4o","stream":true,"stop":["}"],"frequency_penalty":0,"presence_penalty":0}""");
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        using var actual = JsonDocument.Parse(SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", "o3", parsed));
        Assert.AreEqual(2, actual.RootElement.EnumerateObject().Count());
        Assert.AreEqual("o3", actual.RootElement.GetProperty("model").GetString());
        Assert.IsTrue(actual.RootElement.GetProperty("stream").GetBoolean());
    }

    [TestMethod]
    public void MergeModel_ReplacementModel_IsJsonEscaped()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""{"model":"gpt-4o","prompt":"hello world"}""");
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        var newModel = "custom-\"model\\name";
        using var actual = JsonDocument.Parse(SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel("gpt-4o", newModel, parsed));
        Assert.AreEqual(newModel, actual.RootElement.GetProperty("model").GetString());
        Assert.AreEqual("hello world", actual.RootElement.GetProperty("prompt").GetString());
        Assert.AreEqual(3, parsed.WordCount);
    }

    [TestMethod]
    [DataRow("{\"model\":\"gpt-4o\",\"prompt\":\"hello world\"")]
    [DataRow("{\"model\":\"gpt-4o\"} trailing")]
    public void ParseModel_InvalidJson_DiscardsPartialDetection(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var parsed = SimpleL7Proxy.Tokenomics.RequestTransformer.ParseModel(body);
        Assert.IsNull(parsed.SourceModel);
        Assert.AreEqual(0, parsed.WordCount);
        Assert.AreEqual(parsed.OriginalBodyBytes, SimpleL7Proxy.Tokenomics.RequestTransformer.MergeModel(null, "gpt-6-sol", parsed));
    }

    // ---- Field removal and rename maps -------------------------

    [TestMethod]
    [RegressionTestCase("model-override", "Classic-to-reasoning removes sampling fields", "Classic sampling parameters must be removed when routing to reasoning models.")]
    public void FieldRemovalMap_ClassicToReasoning_RemovesSampling()
    {
        var fields = FieldRemovalMap.ClassicToReasoning;

        Assert.IsTrue(fields.Contains("temperature"));
        Assert.IsTrue(fields.Contains("top_p"));
        Assert.IsTrue(fields.Contains("presence_penalty"));
        Assert.IsTrue(fields.Contains("frequency_penalty"));
        Assert.IsTrue(fields.Contains("stop"));
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Classic-to-reasoning renames max_tokens", "Classic max_tokens must be renamed to max_completion_tokens for reasoning models.")]
    public void FieldRenameMap_ClassicToReasoning_RenamesTokenField()
    {
        var fields = FieldRenameMap.ClassicToReasoning;

        Assert.IsTrue(fields.ContainsKey("max_tokens"));
        Assert.AreEqual("max_completion_tokens", fields["max_tokens"]);
    }

    [TestMethod]
    [RegressionTestCase("model-override", "GPT-5-to-classic renames max_completion_tokens back", "Reasoning max_completion_tokens must be restored to max_tokens for classic models.")]
    public void FieldRenameMap_ReasoningToClassic_RenamesBackToMaxTokens()
    {
        var fields = FieldRenameMap.ReasoningToClassic;

        Assert.IsTrue(fields.ContainsKey("max_completion_tokens"));
        Assert.AreEqual("max_tokens", fields["max_completion_tokens"]);
    }
}
