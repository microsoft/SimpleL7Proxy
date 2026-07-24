using System.Text;
using System.Text.Json;
using SimpleL7Proxy.Llm;

namespace SimpleL7Proxy.Test;

/// <summary>
/// Tests for the LLM model/field remapper: <see cref="ModelMap"/> family
/// transitions and <see cref="ModelSwapper.ValidateModel"/> detect and rewrite paths.
/// </summary>
[TestClass]
public sealed class ModelRemapTests
{
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
    public void ModelMap_ClassicToGpt5_UsesClassicToReasoningMaps()
    {
        var (remove, rename) = ModelMap.Get("gpt-4o", "gpt-5");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    [TestMethod]
    public void ModelMap_ClassicToReasoning_UsesClassicToReasoningMaps()
    {
        var (remove, rename) = ModelMap.Get("gpt-4.1", "o3");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    [TestMethod]
    public void ModelMap_Gpt5ToClassic_RemovesGpt5FieldsAndRenamesToClassic()
    {
        var (remove, rename) = ModelMap.Get("gpt-5-mini", "gpt-4o");

        Assert.AreSame(FieldRemovalMap.Gpt5ToClassic, remove);
        Assert.AreSame(FieldRenameMap.ReasoningToClassic, rename);
    }

    [TestMethod]
    public void ModelMap_Gpt5ToReasoning_RemovesVerbosityNoRename()
    {
        var (remove, rename) = ModelMap.Get("gpt-5", "o4-mini");

        Assert.AreSame(FieldRemovalMap.Gpt5ToReasoning, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    public void ModelMap_ReasoningToClassic_UsesReasoningToClassicMaps()
    {
        var (remove, rename) = ModelMap.Get("o3", "gpt-4");

        Assert.AreSame(FieldRemovalMap.ReasoningToClassic, remove);
        Assert.AreSame(FieldRenameMap.ReasoningToClassic, rename);
    }

    [TestMethod]
    public void ModelMap_SameFamily_NoTransform()
    {
        var (remove, rename) = ModelMap.Get("gpt-4", "gpt-4o");

        Assert.AreSame(FieldRemovalMap.Empty, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    public void ModelMap_UnknownModel_NoTransform()
    {
        var (remove, rename) = ModelMap.Get("llama-3", "gpt-4");

        Assert.AreSame(FieldRemovalMap.Empty, remove);
        Assert.AreSame(FieldRenameMap.Empty, rename);
    }

    [TestMethod]
    public void ModelMap_FamilyDetection_IsCaseInsensitive()
    {
        var (remove, rename) = ModelMap.Get("GPT-4O", "GPT-5");

        Assert.AreSame(FieldRemovalMap.ClassicToReasoning, remove);
        Assert.AreSame(FieldRenameMap.ClassicToReasoning, rename);
    }

    // ---- ValidateModel: detect-only (no override) ----------------------

    [TestMethod]
    public void Detect_CapturesTopLevelModel_BodyUnchanged()
    {
        const string body = """{"model":"gpt-4o","messages":[]}""";

        var (result, model) = Run(body);

        Assert.AreEqual("gpt-4o", model);
        Assert.AreEqual(body, result); // body returned unchanged
    }

    [TestMethod]
    public void Detect_NestedModelIgnored()
    {
        var (result, model) = Run("""{"payload":{"model":"gpt-4o"},"n":1}""");

        Assert.AreEqual(string.Empty, model); // only top-level "model" is captured
        Assert.AreEqual("""{"payload":{"model":"gpt-4o"},"n":1}""", result);
    }

    [TestMethod]
    public void Detect_ModelAbsent_LeavesModelEmpty()
    {
        var (_, model) = Run("""{"messages":[],"temperature":0.5}""");

        Assert.AreEqual(string.Empty, model);
    }

    [TestMethod]
    public void Detect_EmptyModelValue_NotCaptured()
    {
        var (_, model) = Run("""{"model":"   "}""");

        Assert.AreEqual(string.Empty, model);
    }

    [TestMethod]
    public void Detect_NonObjectBody_ReturnedUnchanged()
    {
        var (result, model) = Run("[1,2,3]");

        Assert.AreEqual(string.Empty, model);
        Assert.AreEqual("[1,2,3]", result);
    }

    [TestMethod]
    public void Detect_MalformedJson_SetsErrorSentinel()
    {
        // Invalid value token, thrown before any "model" property is captured.
        var (_, model) = Run("""{"a": nope}""");

        Assert.AreEqual("Error parsing model", model);
    }

    // ---- ValidateModel: override / rewrite -----------------------------

    [TestMethod]
    public void Override_ReplacesExistingModel()
    {
        var (result, model) = Run("""{"model":"gpt-4o","keep":true}""", "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        Assert.IsTrue(root.GetProperty("keep").GetBoolean());
    }

    [TestMethod]
    public void Override_AddsModelWhenAbsent()
    {
        var (result, model) = Run("""{"keep":1}""", "gpt-5");

        Assert.AreEqual("gpt-5", model);
        var root = Parse(result);
        Assert.AreEqual("gpt-5", root.GetProperty("model").GetString());
        Assert.AreEqual(1, root.GetProperty("keep").GetInt32());
    }

    [TestMethod]
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
    public void Override_SameFamily_NoFieldTransform()
    {
        var (result, _) = Run("""{"model":"gpt-4","temperature":0.2}""", "gpt-4o");

        var root = Parse(result);
        Assert.AreEqual("gpt-4o", root.GetProperty("model").GetString());
        // Classic -> Classic: temperature is preserved
        Assert.AreEqual(0.2, root.GetProperty("temperature").GetDouble(), 0.0001);
    }

    [TestMethod]
    public void Override_FieldRemovalIsCaseInsensitive()
    {
        var (result, _) = Run("""{"model":"gpt-4o","Temperature":0.5,"keep":1}""", "gpt-5");

        var root = Parse(result);
        Assert.IsFalse(root.TryGetProperty("Temperature", out _)); // removed despite different casing
        Assert.AreEqual(1, root.GetProperty("keep").GetInt32());
    }

    [TestMethod]
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
