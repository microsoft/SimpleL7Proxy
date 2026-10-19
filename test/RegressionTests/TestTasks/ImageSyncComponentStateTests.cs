using CompanionApp.Components.Shared;

namespace SimpleL7Proxy.Test;

[TestClass]
public sealed class ImageSyncComponentStateTests
{
    [TestMethod]
    public void SelectingLatestSynchronizesTheSelectedTag()
    {
        var state = CreateState("v2.3.0", ("v2.3.0", "old"), ("v2.4.0", "new"));
        state.SelectedVersion = "v2.4.0";
        state.SelectedForSynchronization = true;

        Assert.AreEqual("v2.4.0", state.GetSynchronizationVersion());
    }

    [TestMethod]
    public void SelectingNewerNonLatestSynchronizesThatVersion()
    {
        var state = CreateState("v2.3.0", ("v2.3.0", "old"), ("v2.3.1", "middle"), ("v2.4.0", "new"));
        state.SelectedVersion = "v2.3.1";
        state.SelectedForSynchronization = true;

        Assert.AreEqual("v2.3.1", state.GetSynchronizationVersion());
    }

    [TestMethod]
    public void OlderVersionCanBeSelectedForRollback()
    {
        var state = CreateState("v2.4.0", ("v2.2.0", "oldest"), ("v2.3.0", "older"), ("v2.4.0", "current"));
        state.SelectedVersion = "v2.3.0";
        state.SelectedForSynchronization = true;

        Assert.AreEqual("v2.3.0", state.GetSynchronizationVersion());
    }

    [TestMethod]
    public void MatchingCurrentVersionDoesNotRequireSynchronization()
    {
        var state = CreateState(
            "v2.3.0",
            ("v2.3.0", "same"),
            ("v2.4.0", "new"),
            targetDigests: new Dictionary<string, string>(StringComparer.Ordinal) { ["v2.3.0"] = "same" });
        state.SelectedVersion = "v2.3.0";
        state.SelectedForSynchronization = true;

        Assert.IsFalse(state.NeedsSynchronization);
        Assert.IsNull(state.GetSynchronizationVersion());
    }

    [TestMethod]
    public void ComponentsKeepIndependentSelectedVersions()
    {
        var companion = CreateState("v2.3.0", ("v2.3.0", "a"), ("v2.4.0", "b"));
        var metrics = CreateState("v1.0.0", ("v1.0.0", "c"), ("v1.1.0", "d"));
        var proxy = CreateState("v2.3.0", ("v2.3.0", "e"), ("v2.3.1", "f"));
        companion.SelectedVersion = "v2.4.0";
        metrics.SelectedVersion = "v1.1.0";
        proxy.SelectedVersion = "v2.3.1";
        companion.SelectedForSynchronization = metrics.SelectedForSynchronization = proxy.SelectedForSynchronization = true;

        CollectionAssert.AreEqual(
            new[] { "v2.4.0", "v1.1.0", "v2.3.1" },
            new[] { companion.GetSynchronizationVersion(), metrics.GetSynchronizationVersion(), proxy.GetSynchronizationVersion() });
    }

    [TestMethod]
    public void UncheckedComponentIsNotSynchronized()
    {
        var state = CreateState("v2.3.0", ("v2.3.0", "old"), ("v2.4.0", "new"));
        state.SelectedVersion = "v2.4.0";

        Assert.IsNull(state.GetSynchronizationVersion());
    }

    [TestMethod]
    public void RefreshPreservesAnAvailableExplicitSelection()
    {
        var state = CreateState("v2.3.0", ("v2.3.0", "old"), ("v2.3.1", "middle"), ("v2.4.0", "new"));
        state.SelectedVersion = "v2.3.1";
        state.SelectedForSynchronization = true;

        state.Refresh(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["v2.3.0"] = "old",
                ["v2.3.1"] = "middle",
                ["v2.4.0"] = "new"
            },
            new Dictionary<string, string>(StringComparer.Ordinal),
            "v2.3.0");

        Assert.AreEqual("v2.3.1", state.SelectedVersion);
        Assert.IsTrue(state.SelectedForSynchronization);
        Assert.IsFalse(state.SelectionWasReset);
    }

    [TestMethod]
    public void EmptyCurrentVersionDefaultsCurrentAndSelectionToLatest()
    {
        var state = CreateState(null, ("v2.3.0", "old"), ("v2.4.0", "new"));

        Assert.AreEqual("v2.4.0", state.CurrentVersion);
        Assert.AreEqual("v2.4.0", state.SelectedVersion);
        Assert.IsFalse(state.NeedsSynchronization);
    }

    [TestMethod]
    public void CurrentVersionsEnvironmentValueParsesComponentAliases()
    {
        var versions = ImageSyncComponentState.ParseCurrentVersions(
            "proxy:2.30, metrics:1.0, healthprobe:1.0, companionApp:1.0");

        Assert.AreEqual("2.30", versions["proxy"]);
        Assert.AreEqual("1.0", versions["metrics"]);
        Assert.AreEqual("1.0", versions["healthprobe"]);
        Assert.AreEqual("1.0", versions["companionApp"]);
    }

    private static ImageSyncComponentState CreateState(
        string? currentVersion,
        (string Version, string Digest) first,
        (string Version, string Digest) second,
        (string Version, string Digest)? third = null,
        IReadOnlyDictionary<string, string>? targetDigests = null)
    {
        var sourceDigests = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [first.Version] = first.Digest,
            [second.Version] = second.Digest
        };
        if (third is { } value)
        {
            sourceDigests[value.Version] = value.Digest;
        }

        var state = new ImageSyncComponentState("SimpleL7Proxy", "simplel7proxy");
        state.Refresh(sourceDigests, targetDigests ?? new Dictionary<string, string>(StringComparer.Ordinal), currentVersion);
        return state;
    }
}
