using System.Text;
using System.Text.Json;
using SimpleL7Proxy.Llm;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Tests for the LLM model/field remapper: <see cref="ModelMap"/> family
/// transitions and <see cref="ModelSwapper.ValidateModel"/> detect and rewrite paths.
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
                "Keeps rerouted requests valid when the proxy switches a request to a different model family.")
        };

    // ---- Helpers -------------------------------------------------------

    private static (string Body, string Model) Run(string body, string? modelOverride = null)
    {
        using var request = new RequestData();
        var input = Encoding.UTF8.GetBytes(body);
        var result = ModelSwapper.ValidateModel(request, input, modelOverride);
        return (Encoding.UTF8.GetString(result.Span), request.Model);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

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

    // ---- ValidateModel: detect-only (no override) ----------------------

    [TestMethod]
    [RegressionTestCase("model-detection", "Top-level model is captured without rewriting", "Reading a valid top-level model must populate request metadata while leaving the original body unchanged.")]
    public void Detect_CapturesTopLevelModel_BodyUnchanged()
    {
        const string body = """{"model":"gpt-4o","messages":[]}""";

        var (result, model) = Run(body);

        Assert.AreEqual("gpt-4o", model);
        Assert.AreEqual(body, result); // body returned unchanged
    }

    [TestMethod]
    [RegressionTestCase("model-detection", "Nested model fields are not treated as routing input", "Only the top-level model field may control routing; nested payload data must be ignored.")]
    public void Detect_NestedModelIgnored()
    {
        var (result, model) = Run("""{"payload":{"model":"gpt-4o"},"n":1}""");

        Assert.AreEqual(string.Empty, model); // only top-level "model" is captured
        Assert.AreEqual("""{"payload":{"model":"gpt-4o"},"n":1}""", result);
    }

    [TestMethod]
    [RegressionTestCase("model-detection", "Missing model leaves routing metadata empty", "Requests without a model field must not invent a model identity.")]
    public void Detect_ModelAbsent_LeavesModelEmpty()
    {
        var (_, model) = Run("""{"messages":[],"temperature":0.5}""");

        Assert.AreEqual(string.Empty, model);
    }

    [TestMethod]
    [RegressionTestCase("model-detection", "Blank model values are ignored", "Whitespace-only model values must not be treated as valid routing metadata.")]
    public void Detect_EmptyModelValue_NotCaptured()
    {
        var (_, model) = Run("""{"model":"   "}""");

        Assert.AreEqual(string.Empty, model);
    }

    [TestMethod]
    [RegressionTestCase("model-detection", "Non-object JSON passes through unchanged", "JSON arrays and other non-object bodies must not be rewritten or assigned a model.")]
    public void Detect_NonObjectBody_ReturnedUnchanged()
    {
        var (result, model) = Run("[1,2,3]");

        Assert.AreEqual(string.Empty, model);
        Assert.AreEqual("[1,2,3]", result);
    }

    [TestMethod]
    [RegressionTestCase("model-detection", "Malformed JSON is visible as a detection error", "Invalid JSON must set an observable error sentinel instead of silently selecting a model.")]
    public void Detect_MalformedJson_SetsErrorSentinel()
    {
        // Invalid value token, thrown before any "model" property is captured.
        var (_, model) = Run("""{"a": nope}""");

        Assert.AreEqual("Error parsing model", model);
    }

    // ---- ValidateModel: override / rewrite -----------------------------

    [TestMethod]
    [RegressionTestCase("model-override", "Override replaces the existing model", "A requested model override must update the routing model and serialized request while preserving unrelated fields.")]
    public void Override_ReplacesExistingModel()
    {
        var (result, model) = Run("""{"model":"gpt-4o","keep":true}""", "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        Assert.IsTrue(root.GetProperty("keep").GetBoolean());
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Override adds a missing model", "A model override must add the top-level model when the original request omitted it.")]
    public void Override_AddsModelWhenAbsent()
    {
        var (result, model) = Run("""{"keep":1}""", "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        Assert.AreEqual(1, root.GetProperty("keep").GetInt32());
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Classic request is rewritten for reasoning models", "Classic-only sampling fields must be removed and max_tokens renamed before sending to a reasoning model.")]
    public void Override_ClassicToReasoning_RemovesAndRenamesFields()
    {
        const string body = """
        {"model":"gpt-4o","max_tokens":100,"temperature":0.5,"top_p":1,"presence_penalty":0.1,"frequency_penalty":0.2,"stop":["x"],"keep":true}
        """;

        var (result, model) = Run(body, "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        // max_tokens renamed to max_completion_tokens (value preserved)
        Assert.AreEqual(100, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.IsFalse(root.TryGetProperty("max_tokens", out _));
        // sampling fields removed
        Assert.IsFalse(root.TryGetProperty("temperature", out _));
        Assert.IsFalse(root.TryGetProperty("top_p", out _));
        Assert.IsFalse(root.TryGetProperty("presence_penalty", out _));
        Assert.IsFalse(root.TryGetProperty("frequency_penalty", out _));
        Assert.IsFalse(root.TryGetProperty("stop", out _));
        // unrelated field preserved
        Assert.IsTrue(root.GetProperty("keep").GetBoolean());
    }

    [TestMethod]
    [RegressionTestCase("model-override", "GPT-5 request is rewritten for classic models", "GPT-5-only reasoning fields must be removed and token fields restored before sending to a classic model.")]
    public void Override_Gpt5ToClassic_RemovesAndRenamesFields()
    {
        const string body = """
        {"model":"gpt-5","max_completion_tokens":50,"reasoning_effort":"high","verbosity":"low","keep":"y"}
        """;

        var (result, model) = Run(body, "gpt-4o");

        Assert.AreEqual("gpt-4o", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-4o", root.GetProperty("model").GetString());
        Assert.AreEqual(50, root.GetProperty("max_tokens").GetInt32());
        Assert.IsFalse(root.TryGetProperty("max_completion_tokens", out _));
        Assert.IsFalse(root.TryGetProperty("reasoning_effort", out _));
        Assert.IsFalse(root.TryGetProperty("verbosity", out _));
        Assert.AreEqual("y", root.GetProperty("keep").GetString());
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Same-family override preserves compatible fields", "Changing to another model in the same family must update the model without deleting valid sampling settings.")]
    public void Override_SameFamily_NoFieldTransform()
    {
        var (result, _) = Run("""{"model":"gpt-4","temperature":0.2}""", "gpt-4o");

        var root = Parse(result);
        Assert.AreEqual("gpt-4o", root.GetProperty("model").GetString());
        // Classic -> Classic: temperature is preserved
        Assert.AreEqual(0.2, root.GetProperty("temperature").GetDouble(), 0.0001);
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Unsupported fields are removed case-insensitively", "Compatibility rewriting must remove unsupported fields even when clients use different property casing.")]
    public void Override_FieldRemovalIsCaseInsensitive()
    {
        var (result, _) = Run("""{"model":"gpt-4o","Temperature":0.5,"keep":1}""", "gpt-5");

        var root = Parse(result);
        Assert.IsFalse(root.TryGetProperty("Temperature", out _)); // removed despite different casing
        Assert.AreEqual(1, root.GetProperty("keep").GetInt32());
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Nested request structures survive overrides", "Messages, metadata, arrays, and null values must remain intact while the top-level model is changed.")]
    public void Override_PreservesNestedStructures()
    {
        const string body = """
        {"model":"gpt-4o","messages":[{"role":"user","content":"hi"}],"meta":{"a":1,"b":[true,null]}}
        """;

        var (result, _) = Run(body, "gpt-4.1"); // Classic -> Classic, no removals

        var root = Parse(result);
        Assert.AreEqual("gpt-4.1", root.GetProperty("model").GetString());
        Assert.AreEqual("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.AreEqual(1, root.GetProperty("meta").GetProperty("a").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("meta").GetProperty("b")[1].ValueKind);
    }

    [TestMethod]
    [RegressionTestCase("model-override", "Override without a source model avoids destructive transforms", "When the source family is unknown, the override must add the model but retain existing request fields.")]
    public void Override_NoSourceModel_AddsModelAndKeepsFields()
    {
        // No top-level model in body -> no family transform, override appended.
        var (result, model) = Run("""{"temperature":0.5,"max_tokens":10}""", "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        // No source model -> fields left untouched (no rename/removal)
        Assert.AreEqual(0.5, root.GetProperty("temperature").GetDouble(), 0.0001);
        Assert.AreEqual(10, root.GetProperty("max_tokens").GetInt32());
    }
}
