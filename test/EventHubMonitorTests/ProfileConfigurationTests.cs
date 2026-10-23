using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionApp.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EventHubMonitorTests;

/// <summary>Checks local profile-file editing and the exported shared-rule contract.</summary>
[TestClass]
public sealed class ProfileConfigurationTests {
    private const string Rule = """
        {"name":"premium","if":{"name":"tier","field":"x-user-tier","match":"equals","value":"premium","ignoreCase":true},
         "then":{"name":"priority","set":{"S7PPriorityKey":"1"}}}
        """;

    [TestMethod]
    public void Export_PutsSharedLibraryLastAndPreservesOrderedAssignments() {
        var document = new ProfileConfigurationDocument();
        document.Import($$$"""
            [{"userId":"123456","Header1":"Value1","AllowedPaths":"/api*, /*",
              "async-config":"enabled=true, containername=user123456, topic=status-123456, timeout=600",
              "S7PRules":["second","premium"]},
             {"userId":"123455","S7PRules":[]},
             {"S7PRules":[{{{Rule}}},{"name":"second","set":{"Branch":"green"}}]}]
            """);
        var output = JsonNode.Parse(document.Export())!.AsArray();
        Assert.AreEqual(3, output.Count);
        Assert.AreEqual(1, output[2]!.AsObject().Count);
        Assert.AreEqual(2, output[2]!["S7PRules"]!.AsArray().Count);
        Assert.AreEqual("second", output[0]!["S7PRules"]![0]!.GetValue<string>());
        Assert.AreEqual("premium", output[0]!["S7PRules"]![1]!.GetValue<string>());
        Assert.AreEqual("/api*, /*", output[0]!["AllowedPaths"]!.GetValue<string>());
        Assert.AreEqual("enabled=true, containername=user123456, topic=status-123456, timeout=600",
            output[0]!["async-config"]!.GetValue<string>());
        var reopened = new ProfileConfigurationDocument();
        reopened.Import(document.Export());
        Assert.AreEqual(document.Export(), reopened.Export());
    }

    [TestMethod]
    public void Import_MigratesLegacyInlineRules() {
        var document = new ProfileConfigurationDocument();
        document.Import($$"""[{"userId":"one","rules":[{{Rule}}]},{"userId":"two","rules":[{{Rule}}]}]""");
        Assert.AreEqual(1, document.Rules.Count);
        Assert.AreEqual("premium", document.Profiles[0].RuleNames.Single());
        Assert.AreEqual("premium", document.Profiles[1].RuleNames.Single());
        Assert.IsFalse(document.Export().Contains("\"rules\":", StringComparison.Ordinal));
        document.Import($$"""[{"userId":"one","rules":[{{Rule}}]},{"S7PRules":[{{Rule}}]}]""");
        Assert.AreEqual(1, document.Rules.Count);
    }

    [TestMethod]
    public void Import_RejectsInvalidInputWithoutReplacingCurrentDraft() {
        var document = new ProfileConfigurationDocument();
        document.Import("""[{"userId":"existing","Header1":"original"}]""");
        var before = document.Export();
        foreach (var invalid in new[] {
            """{}""",
            """[{"userId":"one"},{"userId":"one"}]""",
            """[{"userId":"one","Header":"a","header":"b"}]""",
            """[{"userId":"one","S7PRules":["unknown"]}]""",
            """[{"S7PRules":[]},{"userId":"one"}]""",
            """[{"userId":"one","Header":true}]""",
            """[{"userId":"one","Header":"value\r\nInjected: true"}]""",
            """[{"userId":"one","S7PRules":null}]""",
            """[{"userId":"one","async-config":"enabled=true"}]""",
            """[{"userId":"one","async-config":"enabled=true, containername=INVALID, topic=status"}]""",
            """[{"userId":"one","async-config":"enabled=true, containername=valid, topic=-status"}]"""
        }) {
            Assert.ThrowsException<ArgumentException>(() => document.Import(invalid), invalid);
            Assert.AreEqual(before, document.Export());
        }
    }

    [TestMethod]
    public void Export_RejectsDuplicateReservedAndEmptyFields() {
        var document = new ProfileConfigurationDocument();
        var profile = new ProfileConfigurationEntry { Id = "one" };
        document.Profiles.Add(profile);
        foreach (var name in new[] { "", "userId", "S7PRules", "rules", "invalid header", "bad:header" }) {
            profile.Fields.Clear();
            profile.Fields.Add(new() { Name = name, Value = "value" });
            Assert.ThrowsException<ArgumentException>(() => document.Export(), name);
        }
        profile.Fields.Clear();
        profile.Fields.Add(new() { Name = "Header", Value = "a" });
        profile.Fields.Add(new() { Name = "header", Value = "b" });
        Assert.ThrowsException<ArgumentException>(() => document.Export());
    }

    [TestMethod]
    public void RuleValidation_RoundTripsBranchesAndRejectsMalformedDefinitions() {
        var rule = ProfileConfigurationDocument.ParseRule("""
            {"name":"routing","if":{"name":"blue","field":"S7PHash","match":"between","value":"0","value2":"25","mode":"inClosedOpenRange"},
             "then":{"name":"blue-result","set":{"Branch":"blue"}},
             "elseif":[{"name":"green-clause","if":{"name":"green","field":"S7PHash","match":"between","value":"25","value2":"40","mode":"inClosedOpenRange"},
                        "then":{"name":"green-result","set":{"Branch":"green"}}}],
             "else":{"name":"fallback","set":{"Branch":"red"}}}
            """);
        var document = new ProfileConfigurationDocument();
        document.Rules.Add(rule);
        var reopened = new ProfileConfigurationDocument();
        reopened.Import(document.Export());
        Assert.IsTrue(JsonNode.DeepEquals(rule, reopened.Rules.Single()));
        foreach (var invalid in new[] {
            """{"name":"bad","if":null,"then":{"name":"x","set":{}}}""",
            """{"name":"bad","if":{"name":"x","field":"h","match":"invalid"},"then":{"name":"x","set":{}}}""",
            """{"name":"bad","if":{"name":"x","field":"h","match":"regex","value":"["},"then":{"name":"x","set":{}}}""",
            """{"name":"bad","set":{"Header":42}}""",
            """{"name":"bad","set":{},"elseif":null}""",
            """{"name":"bad","set":{},"ELSEIF":null}""",
            """{"name":"bad","set":{"Header":"one\nInjected: true"}}""",
            """{"name":"bad","set":{"Header":"one"},"SET":{"Header":"two"}}""",
            """{"name":"bad","if":{"name":"x","field":"h","value":null},"then":{"name":"x","set":{}}}""",
            """{"name":"bad","if":{"name":"x","field":"h","match":"between","value":"10","value2":" "},"then":{"name":"x","set":{}}}""",
            """{"name":"bad","if":{"name":"x","field":"h","match":"between","value2":"20"},"then":{"name":"x","set":{}}}""",
            """{"name":"bad","if":{"name":"x","field":"h","match":"between","value":"10"},"then":{"name":"x","set":{}}}"""
        }) {
            Assert.ThrowsException<ArgumentException>(() => ProfileConfigurationDocument.ParseRule(invalid), invalid);
        }
    }

    [TestMethod]
    public void Export_EmptyDocumentStillContainsFinalRuleLibrary() {
        var output = JsonNode.Parse(new ProfileConfigurationDocument().Export())!.AsArray();
        Assert.AreEqual(1, output.Count);
        Assert.AreEqual(0, output[0]!["S7PRules"]!.AsArray().Count);
    }

    [TestMethod]
    public void Import_RespectsExplicitIdentityField() {
        var document = new ProfileConfigurationDocument { UserIdField = "employeeId" };
        document.Import("""[{"employeeId":"one","Header1":"Value1"}]""");
        Assert.AreEqual("one", document.Profiles.Single().Id);
        Assert.AreEqual("one", JsonNode.Parse(document.Export())![0]!["employeeId"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Editor_RenamesAssignmentsAndKeepsInvalidRuleBuffer() {
        var state = new ProfileConfigurationEditor.EditorState();
        state.Document.Import($$"""[{"userId":"one","S7PRules":["premium"]},{"S7PRules":[{{Rule}}]}]""");
        state.SelectedRule = state.Document.Rules.Single();
        state.RuleJson = state.SelectedRule.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var editor = new ProfileConfigurationEditor();
        typeof(ProfileConfigurationEditor).GetProperty(nameof(ProfileConfigurationEditor.State))!.SetValue(editor, state);
        state.RuleJson = state.RuleJson.Replace("\"premium\"", "\"renamed\"", StringComparison.Ordinal);
        Assert.AreEqual(true, Invoke(editor, "ApplyRule"));
        Assert.AreEqual("renamed", state.Document.Profiles[0].RuleNames.Single());
        Assert.IsTrue(state.IsDirty);

        var selected = state.SelectedRule;
        await (Task)Invoke(editor, "SelectRuleAsync", selected!)!;
        Assert.AreSame(selected, state.SelectedRule);
        state.RuleJson = "{ invalid";
        Assert.AreEqual(false, Invoke(editor, "ApplyRule"));
        Assert.AreSame(selected, state.SelectedRule);
        Assert.AreEqual("{ invalid", state.RuleJson);
        Assert.IsNotNull(state.Error);
    }

    [TestMethod]
    public void Editor_AsyncControlsPreserveOptionalSettingsAndRuleOrderChanges() {
        var state = new ProfileConfigurationEditor.EditorState();
        var profile = new ProfileConfigurationEntry { Id = "one" };
        profile.Fields.Add(new() { Name = "async-config", Value = "enabled=true, timeout=600, generatesas=true, containername=old" });
        profile.RuleNames.AddRange(["first", "second", "third"]);
        var editor = new ProfileConfigurationEditor();
        typeof(ProfileConfigurationEditor).GetProperty(nameof(ProfileConfigurationEditor.State))!.SetValue(editor, state);
        Invoke(editor, "SetAsyncValue", profile, "containername", "newcontainer");
        StringAssert.Contains(profile.Fields[0].Value, "timeout=600");
        StringAssert.Contains(profile.Fields[0].Value, "generatesas=true");
        StringAssert.Contains(profile.Fields[0].Value, "containername=newcontainer");
        Invoke(editor, "MoveRule", profile, 1, -1);
        CollectionAssert.AreEqual(new[] { "second", "first", "third" }, profile.RuleNames);
    }

    [TestMethod]
    public void Editor_IdentityChangesAreExplicitAndCancelRestoresIdentityAndRuleBuffer() {
        var editor = new ProfileConfigurationEditor();
        typeof(ProfileConfigurationEditor).GetProperty(nameof(ProfileConfigurationEditor.IdentityField))!.SetValue(editor, "employeeId");
        Invoke(editor, "OnParametersSet");
        var state = editor.State;
        Assert.AreEqual("employeeId", state.Document.UserIdField);
        state.Document.Import($$"""[{"employeeId":"one","S7PRules":["premium"]},{"S7PRules":[{{Rule}}]}]""");
        state.SavedJson = state.Document.Export();
        state.SavedUserIdField = state.Document.UserIdField;
        state.Document.UserIdField = "newId";
        state.RuleJson = "{ invalid";
        state.IsDirty = true;
        Invoke(editor, "OnParametersSet");
        Assert.AreEqual("newId", state.Document.UserIdField, "Settings must not silently rewrite a nonempty file draft.");
        state.RestoreSaved();
        Assert.AreEqual("employeeId", state.Document.UserIdField);
        Assert.IsFalse(state.IsDirty);
        Assert.IsNotNull(state.SelectedRule);
        ProfileConfigurationDocument.ParseRule(state.RuleJson);
    }

    [TestMethod]
    public void Editor_DefaultsToVisualAndKeepsJsonOptIn() {
        var editor = CreateRuleEditor();
        var state = editor.State;
        Assert.IsFalse(state.EditRuleJson);
        Assert.IsNotNull(state.RuleDraft);
        Assert.AreNotSame(state.SelectedRule, state.RuleDraft);
        Assert.IsFalse(state.IsDirty);

        // Inspect the generated controls without mounting an Interactive Server page.
#pragma warning disable BL0006
        using var builder = new RenderTreeBuilder();
        typeof(ProfileConfigurationEditor).GetMethod("BuildRenderTree", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(editor, [builder]);
        var frames = builder.GetFrames();
        Assert.IsTrue(frames.Array.Take(frames.Count).Any(frame =>
            frame.FrameType == RenderTreeFrameType.Component && frame.ComponentType == typeof(ProfileRuleNodeEditor)));
        Assert.IsFalse(frames.Array.Take(frames.Count).Any(frame =>
            frame.FrameType == RenderTreeFrameType.Element && frame.ElementName == "textarea"));
#pragma warning restore BL0006

        Invoke(editor, "ToggleRuleEditor");
        Assert.IsTrue(state.EditRuleJson);
        state.RuleJson = state.RuleJson.Replace("x-user-tier", "x-region", StringComparison.Ordinal);
        Invoke(editor, "ToggleRuleEditor");
        Assert.IsFalse(state.EditRuleJson);
        Assert.AreEqual("x-region", state.RuleDraft!["if"]!["field"]!.GetValue<string>());
        Assert.AreEqual("x-user-tier", state.SelectedRule!["if"]!["field"]!.GetValue<string>());
    }

    [TestMethod]
    public void Editor_InvalidJsonCannotSwitchToVisualOrLoseBuffer() {
        var editor = CreateRuleEditor();
        Invoke(editor, "ToggleRuleEditor");
        Invoke(editor, "SetRuleJson", "{ invalid");
        Invoke(editor, "ToggleRuleEditor");
        Assert.IsTrue(editor.State.EditRuleJson);
        Assert.AreEqual("{ invalid", editor.State.RuleJson);
        Assert.IsNotNull(editor.State.Error);
        Assert.AreEqual("premium", ProfileConfigurationDocument.RuleName(editor.State.SelectedRule!));
        Invoke(editor, "ResetRuleBuffer");
        Invoke(editor, "ToggleRuleEditor");
        Assert.IsFalse(editor.State.EditRuleJson);
        Assert.IsNull(editor.State.Error);
    }

    [TestMethod]
    public async Task Editor_VisualChangesRenameAssignmentsAndCancelRestoresSavedFile() {
        var editor = CreateRuleEditor();
        var state = editor.State;
        var original = state.SelectedRule!.ToJsonString();
        var nodeEditor = CreateNodeEditor(state.RuleDraft!, editor);
        await InvokeNode(nodeEditor, "SetTextAsync", state.RuleDraft!, "name", "renamed");
        await InvokeNode(nodeEditor, "SetTextAsync", state.RuleDraft!["if"]!.AsObject(), "value", "enterprise");
        await InvokeNode(nodeEditor, "SetIgnoreCaseAsync", state.RuleDraft!["if"]!.AsObject(), false);
        Assert.IsTrue(state.IsDirty);
        Assert.AreEqual(original, state.SelectedRule.ToJsonString(), "Visual edits must remain buffered until Apply or Save.");
        Assert.AreEqual(true, Invoke(editor, "ApplyRule"));
        Assert.AreEqual("renamed", state.Document.Profiles.Single().RuleNames.Single());
        Assert.IsFalse(state.SelectedRule!["if"]!["ignoreCase"]!.GetValue<bool>());
        state.RestoreSaved();
        Assert.IsFalse(state.IsDirty);
        Assert.IsFalse(state.EditRuleJson);
        Assert.AreEqual("premium", state.Document.Profiles.Single().RuleNames.Single());
        Assert.AreEqual(original, state.RuleDraft!.ToJsonString());
    }

    [TestMethod]
    public async Task Editor_VisualFieldsPreserveValuesAndRejectDuplicateNames() {
        var editor = CreateRuleEditor();
        var node = editor.State.RuleDraft!["then"]!.AsObject();
        var fields = node["set"]!.AsObject();
        var nodeEditor = CreateNodeEditor(node, editor);
        await InvokeNode(nodeEditor, "AddFieldAsync", fields);
        await InvokeNode(nodeEditor, "SetTextAsync", fields, "Header1", "gpt-4o");
        await InvokeNode(nodeEditor, "RenameFieldAsync", fields, "Header1", "S7P-ModelOverride");
        await InvokeNode(nodeEditor, "RenameFieldAsync", fields, "S7P-ModelOverride", "s7pprioritykey");
        Assert.AreEqual("gpt-4o", fields["S7P-ModelOverride"]!.GetValue<string>());
        Assert.AreEqual("1", fields["S7PPriorityKey"]!.GetValue<string>());
        await InvokeNode(nodeEditor, "RenameFieldAsync", fields, "S7P-ModelOverride", "bad header");
        Assert.IsTrue(fields.ContainsKey("S7P-ModelOverride"));
        await InvokeNode(nodeEditor, "RemoveAsync", fields, "S7P-ModelOverride");
        Assert.IsFalse(fields.ContainsKey("S7P-ModelOverride"));
        Assert.AreEqual(true, Invoke(editor, "ApplyRule"));
        Assert.IsFalse(editor.State.SelectedRule!["then"]!["set"]!.AsObject().ContainsKey("S7P-ModelOverride"));
    }

    [TestMethod]
    public async Task Editor_VisualNestedBranchesAndRangeConditionsRoundTrip() {
        var editor = CreateRuleEditor();
        var root = editor.State.RuleDraft!;
        var nodeEditor = CreateNodeEditor(root, editor);
        await InvokeNode(nodeEditor, "AddNestedRuleAsync");
        await InvokeNode(nodeEditor, "AddElseIfAsync");
        var nested = root["else"]!.AsObject();
        var nestedEditor = CreateNodeEditor(nested, editor);
        await InvokeNode(nestedEditor, "SetTextAsync", nested["if"]!.AsObject(), "match", "between");
        await InvokeNode(nestedEditor, "SetTextAsync", nested["if"]!.AsObject(), "value", "10");
        await InvokeNode(nestedEditor, "SetTextAsync", nested["if"]!.AsObject(), "value2", "20");
        await InvokeNode(nestedEditor, "SetTextAsync", nested["if"]!.AsObject(), "mode", "inClosedOpenRange");
        await InvokeNode(nestedEditor, "AddElseAsync");
        Assert.AreEqual(true, Invoke(editor, "ApplyRule"));
        var reopened = new ProfileConfigurationDocument();
        reopened.Import(editor.State.Document.Export());
        Assert.IsTrue(JsonNode.DeepEquals(root, reopened.Rules.Single()));
        Assert.AreEqual(1, root["elseif"]!.AsArray().Count);
        Assert.IsNotNull(nested["else"]);
    }

    [TestMethod]
    public async Task Editor_VisualDepthLimitPreventsAddingUnsaveableBranches() {
        var node = ProfileConfigurationDocument.ParseRule(Rule);
        var nodeEditor = new ProfileRuleNodeEditor();
        typeof(ProfileRuleNodeEditor).GetProperty(nameof(ProfileRuleNodeEditor.Node))!.SetValue(nodeEditor, node);
        typeof(ProfileRuleNodeEditor).GetProperty(nameof(ProfileRuleNodeEditor.Depth))!.SetValue(nodeEditor, 15);
        await InvokeNode(nodeEditor, "AddNestedRuleAsync");
        await InvokeNode(nodeEditor, "AddElseIfAsync");
        Assert.IsNull(node["else"]);
        Assert.IsNull(node["elseif"]);
        await InvokeNode(nodeEditor, "AddElseAsync");
        Assert.IsNotNull(node["else"]);
        node.Remove("else");
        typeof(ProfileRuleNodeEditor).GetProperty(nameof(ProfileRuleNodeEditor.Depth))!.SetValue(nodeEditor, 16);
        await InvokeNode(nodeEditor, "AddElseAsync");
        Assert.IsNull(node["else"]);
    }

    private static ProfileConfigurationEditor CreateRuleEditor() {
        var editor = new ProfileConfigurationEditor();
        editor.State.Document.Import($$"""[{"userId":"one","S7PRules":["premium"]},{"S7PRules":[{{Rule}}]}]""");
        editor.State.SavedJson = editor.State.Document.Export();
        editor.State.SelectedRule = editor.State.Document.Rules.Single();
        typeof(ProfileConfigurationEditor).GetProperty(nameof(ProfileConfigurationEditor.View))!.SetValue(editor, "rules");
        Invoke(editor, "ResetRuleBuffer");
        return editor;
    }

    private ProfileRuleNodeEditor CreateNodeEditor(JsonObject node, ProfileConfigurationEditor editor) {
        var nodeEditor = new ProfileRuleNodeEditor();
        typeof(ProfileRuleNodeEditor).GetProperty(nameof(ProfileRuleNodeEditor.Node))!.SetValue(nodeEditor, node);
        typeof(ProfileRuleNodeEditor).GetProperty(nameof(ProfileRuleNodeEditor.OnChanged))!
            .SetValue(nodeEditor, EventCallback.Factory.Create(this, () => Invoke(editor, "UpdateVisualRule")));
        return nodeEditor;
    }

    private static Task InvokeNode(ProfileRuleNodeEditor editor, string method, params object[] arguments) =>
        (Task)typeof(ProfileRuleNodeEditor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, arguments)!;

    private static object? Invoke(ProfileConfigurationEditor editor, string method, params object[] arguments) =>
        typeof(ProfileConfigurationEditor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, arguments);
}
