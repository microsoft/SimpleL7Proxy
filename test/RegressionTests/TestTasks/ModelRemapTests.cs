using System.Text.Json;
using SimpleL7Proxy.Tokenomics.Llm;
using SimpleL7Proxy.Tokenomics;

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
