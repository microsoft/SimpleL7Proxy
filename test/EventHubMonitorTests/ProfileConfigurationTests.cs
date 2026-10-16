using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionApp.Components.Shared;
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
            """{"name":"bad","if":{"name":"x","field":"h","value":null},"then":{"name":"x","set":{}}}"""
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

    private static object? Invoke(ProfileConfigurationEditor editor, string method, params object[] arguments) =>
        typeof(ProfileConfigurationEditor).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, arguments);
}
