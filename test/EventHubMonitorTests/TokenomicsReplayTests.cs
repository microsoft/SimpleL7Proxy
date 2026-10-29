using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using CompanionApp.Components.Shared;
using CompanionApp.Components.Shared.EventHub;
using CompanionApp.Tokenomics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Tokenomics;
using SimpleL7Proxy.Tokenomics.Llm;

namespace EventHubMonitorTests;

/// <summary>Exercises startup validation, local cadence and immutable replay aggregation.</summary>
[TestClass]
public sealed class TokenomicsReplayTests {
    private string _directory = null!;

    [TestInitialize]
    public void Initialize() {
        _directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", $"tokenomics-replay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public void Arguments_PreserveNormalAndUiOnlyFlags_AndRejectInvalidReplay() {
        var normal = TokenomicsReplayOptions.Parse(["--uionly", "--urls", "http://localhost:5259"]);
        Assert.IsNull(normal.FileName);
        CollectionAssert.AreEqual(new[] { "--uionly", "--urls", "http://localhost:5259" }, normal.ApplicationArgs);
        var file = WriteFile("");
        var replay = TokenomicsReplayOptions.Parse(["--uionly", "--run", "events", file, "--urls", "http://localhost:5259"]);
        CollectionAssert.AreEqual(normal.ApplicationArgs, replay.ApplicationArgs);
        Assert.AreEqual(file, replay.FileName);
        foreach (var args in new[] {
            new[] { "--run" }, new[] { "--run", "events" }, new[] { "--run", "other", file },
            new[] { "--run", "events", "--uionly" }, new[] { "--run=events" },
            new[] { "--run", "events", file, "--run", "events", file }
        }) {
            Assert.ThrowsException<ArgumentException>(() => TokenomicsReplayOptions.Parse(args));
        }
    }

    [TestMethod]
    public void Settings_NewPeriodFields_RoundTripThroughStringFormat() {
        var source = new TokenomicsSettings {
            DefaultModelAPI = "OpenAI Chat Completions",
            DefaultLimitsAndBudgets = new LimitsAndBudgets {
                HourlyTokenLimit = 321,
                HourlyBudgetUsd = 12.345m
            },
            PrivilegedExceedActions = new ExceededActions {
                HourlyLimitAction = TokenActionEnum.IncreaseLimit,
                DailyLimitAction = TokenActionEnum.Bypass,
                MonthlyLimitAction = TokenActionEnum.Cap,
                HourlyBudgetExceededAction = TokenActionEnum.UpgradeModel
            },
            DefaultExceededActions = new ExceededActions {
                HourlyLimitAction = TokenActionEnum.Throttle,
                HourlyBudgetExceededAction = TokenActionEnum.Reject
            },
            ModelHostOptions = new Dictionary<string, Dictionary<string, TokenomicsHostOptions>>(StringComparer.OrdinalIgnoreCase) {
                ["gpt-4o"] = new Dictionary<string, TokenomicsHostOptions>(StringComparer.OrdinalIgnoreCase) {
                    ["Host_apim"] = new TokenomicsHostOptions {
                        APIs = new Dictionary<string, TokenomicsApiOptions>(StringComparer.OrdinalIgnoreCase) {
                            ["OpenAI Chat Completions"] = new TokenomicsApiOptions {
                                OverridePath = true,
                                Path = "/custom/chat"
                            },
                            ["OpenAI Responses"] = new TokenomicsApiOptions {
                                PassThrough = true
                            },
                            ["Anthropic Messages"] = new TokenomicsApiOptions()
                        }
                    },
                    ["Host_disabled"] = new TokenomicsHostOptions { Enabled = false }
                }
            }
        };

        var serialized = source.ToString();

        StringAssert.Contains(serialized, "DefaultModelAPI=OpenAI%20Chat%20Completions");
        StringAssert.Contains(serialized, "HourlyTokenLimit=321");
        StringAssert.Contains(serialized, "HourlyBudgetUsd=12.345");
        StringAssert.Contains(serialized, "PrivilegedExceedActions.HourlyLimitAction=increaseLimit");
        StringAssert.Contains(serialized, "PrivilegedExceedActions.DailyLimitAction=bypass");
        StringAssert.Contains(serialized, "PrivilegedExceedActions.MonthlyLimitAction=cap");
        StringAssert.Contains(serialized, "PrivilegedExceedActions.HourlyBudgetExceededAction=upgradeModel");
        StringAssert.Contains(serialized, "HourlyLimitAction=throttle");
        StringAssert.Contains(serialized, "HourlyBudgetExceededAction=reject");
        StringAssert.Contains(serialized, "ModelHostOptions=gpt-4o=Host_apim=Enabled:true");
        StringAssert.Contains(serialized, "ModelHostOptions=gpt-4o=Host_disabled=Enabled:false");
        StringAssert.Contains(serialized, "Api:OpenAI%20Chat%20Completions:false:true:%2Fcustom%2Fchat");
        StringAssert.Contains(serialized, "Api:OpenAI%20Responses:true:false");
        StringAssert.Contains(serialized, "Api:Anthropic%20Messages:false:false");

        var parsed = new TokenomicsSettings();
        Assert.IsTrue(parsed.TryParse(serialized));
        Assert.AreEqual(source.DefaultModelAPI, parsed.DefaultModelAPI);
        Assert.AreEqual(source.DefaultLimitsAndBudgets.HourlyTokenLimit, parsed.DefaultLimitsAndBudgets.HourlyTokenLimit);
        Assert.AreEqual(source.DefaultLimitsAndBudgets.HourlyBudgetUsd, parsed.DefaultLimitsAndBudgets.HourlyBudgetUsd);
        Assert.AreEqual(source.PrivilegedExceedActions.HourlyLimitAction, parsed.PrivilegedExceedActions.HourlyLimitAction);
        Assert.AreEqual(source.PrivilegedExceedActions.DailyLimitAction, parsed.PrivilegedExceedActions.DailyLimitAction);
        Assert.AreEqual(source.PrivilegedExceedActions.MonthlyLimitAction, parsed.PrivilegedExceedActions.MonthlyLimitAction);
        Assert.AreEqual(source.DefaultExceededActions.HourlyLimitAction, parsed.DefaultExceededActions.HourlyLimitAction);
        Assert.AreEqual(source.DefaultExceededActions.HourlyBudgetExceededAction, parsed.DefaultExceededActions.HourlyBudgetExceededAction);
        Assert.AreEqual(source.PrivilegedExceedActions.HourlyBudgetExceededAction, parsed.PrivilegedExceedActions.HourlyBudgetExceededAction);
        CollectionAssert.AreEquivalent(
            source.ModelHostOptions["gpt-4o"].Keys.ToArray(),
            parsed.ModelHostOptions["gpt-4o"].Keys.ToArray());
        Assert.IsFalse(parsed.ModelHostOptions["gpt-4o"]["Host_disabled"].Enabled);
        var parsedApis = parsed.ModelHostOptions["gpt-4o"]["Host_apim"].APIs;
        Assert.IsFalse(parsedApis["OpenAI Chat Completions"].PassThrough);
        Assert.IsTrue(parsedApis["OpenAI Chat Completions"].OverridePath);
        Assert.AreEqual("/custom/chat", parsedApis["OpenAI Chat Completions"].Path);
        Assert.IsTrue(parsedApis["OpenAI Responses"].PassThrough);
        Assert.IsFalse(parsedApis["OpenAI Responses"].OverridePath);
        Assert.IsFalse(parsedApis["Anthropic Messages"].PassThrough);
        Assert.IsFalse(parsedApis["Anthropic Messages"].OverridePath);
        Assert.IsNull(parsedApis["Anthropic Messages"].Path);

        var semicolonSeparated = new TokenomicsSettings();
        Assert.IsTrue(semicolonSeparated.TryParse(
            "ModelHostOptions=my-aunt-jane=Host_apim-a=Enabled:true;ModelHostOptions=my-aunt-jane=Host_apim-b=Enabled:false;DailyTokenLimit=25"));
        CollectionAssert.AreEquivalent(
            new[] { "Host_apim-a", "Host_apim-b" },
            semicolonSeparated.ModelHostOptions["my-aunt-jane"].Keys.ToArray());
        Assert.AreEqual(25, semicolonSeparated.DefaultLimitsAndBudgets.DailyTokenLimit);
        Assert.IsTrue(semicolonSeparated.TryParse("AllowedHosts=my-aunt-jane=apim-a,apim-b"));
        CollectionAssert.Contains(semicolonSeparated.IgnoredFields.ToArray(), "AllowedHosts");
    }

    [TestMethod]
    public void Settings_InvalidPart_IsReportedWhileValidPartsApply() {
        var settings = new TokenomicsSettings {
            DefaultLimitsAndBudgets = new LimitsAndBudgets { DailyTokenLimit = 25 }
        };
        settings.ModelHostOptions["gpt-4o"] = new Dictionary<string, TokenomicsHostOptions>(StringComparer.OrdinalIgnoreCase) {
            ["Host_apim"] = new TokenomicsHostOptions { Enabled = false }
        };

        Assert.IsTrue(settings.TryParse("DailyTokenLimit=50;ModelHostOptions=gpt-4o=Host_apim=Enabled:true;NotARealProperty=1"));
        Assert.AreEqual(50, settings.DefaultLimitsAndBudgets.DailyTokenLimit);
        CollectionAssert.AreEqual(new[] { "NotARealProperty" }, settings.IgnoredFields.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "Host_apim" },
            settings.ModelHostOptions["gpt-4o"].Keys.ToArray());
        Assert.IsTrue(settings.ModelHostOptions["gpt-4o"]["Host_apim"].Enabled);
    }

    /// <summary>Every workload field retains its own scope and canonical value through serialization.</summary>
    [TestMethod]
    public void Settings_WorkloadFields_RoundTripThroughDottedKeys() {
        var source = new TokenomicsSettings();
        var scopeNames = new[] {
            nameof(TokenomicsSettings.PremiumLimitsAndBudgets),
            nameof(TokenomicsSettings.PremiumExceedActions),
            nameof(TokenomicsSettings.PrivilegedLimitsAndBudgets),
            nameof(TokenomicsSettings.PrivilegedExceedActions)
        };
        var actions = new[] {
            TokenActionEnum.Bypass, TokenActionEnum.Reject, TokenActionEnum.Delay,
            TokenActionEnum.Requeue, TokenActionEnum.Throttle, TokenActionEnum.IncreaseLimit
        };
        for (var scopeIndex = 0; scopeIndex < scopeNames.Length; scopeIndex++) {
            var scope = typeof(TokenomicsSettings).GetProperty(scopeNames[scopeIndex])!.GetValue(source)!;
            var fields = scope.GetType().GetProperties();
            for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++) {
                object value = fields[fieldIndex].PropertyType == typeof(int)
                    ? (object)(2000 + scopeIndex * 100 + fieldIndex)
                    : fields[fieldIndex].PropertyType == typeof(decimal)
                        ? 20.125m + scopeIndex + fieldIndex
                        : actions[(scopeIndex + fieldIndex) % actions.Length];
                fields[fieldIndex].SetValue(scope, value);
            }
        }

        var previousCulture = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var serialized = source.ToString();
            var parts = serialized.Split("; ", StringSplitOptions.None);
            var parsed = new TokenomicsSettings();
            Assert.IsTrue(parsed.TryParse(serialized));
            Assert.AreEqual(serialized, parsed.ToString());
            foreach (var scopeName in scopeNames) {
                var property = typeof(TokenomicsSettings).GetProperty(scopeName)!;
                var originalScope = property.GetValue(source)!;
                var parsedScope = property.GetValue(parsed)!;
                Assert.AreNotSame(originalScope, parsedScope);
                foreach (var field in originalScope.GetType().GetProperties()) {
                    var expected = field.GetValue(originalScope)!;
                    Assert.AreEqual(expected, field.GetValue(parsedScope), $"{scopeName}.{field.Name}");
                    Assert.AreEqual(1, parts.Count(part => part.StartsWith($"{scopeName}.{field.Name}=", StringComparison.Ordinal)));
                }
            }
            Assert.AreEqual(new LimitsAndBudgets().HourlyTokenLimit, parsed.DefaultLimitsAndBudgets.HourlyTokenLimit);
            Assert.AreEqual(new ExceededActions().HourlyLimitAction, parsed.DefaultExceededActions.HourlyLimitAction);
        }
        finally {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>Partial workload updates preserve omitted fields, defaults, and model override syntax.</summary>
    [TestMethod]
    public void Settings_WorkloadUpdates_PreserveIndependentScopesAndOmittedFields() {
        var settings = new TokenomicsSettings();
        Assert.IsTrue(settings.TryParse(
            "PremiumLimitsAndBudgets.HourlyTokenLimit=2100;PremiumLimitsAndBudgets.DailyTokenLimit=2200;" +
            "PrivilegedLimitsAndBudgets.HourlyTokenLimit=3100;PrivilegedLimitsAndBudgets.DailyTokenLimit=3200;" +
            "PremiumExceedActions.DailyLimitAction=reject;" +
            "PrivilegedExceedActions.DailyLimitAction=bypass"));
        var premiumLimits = settings.PremiumLimitsAndBudgets;
        var criticalLimits = settings.PrivilegedLimitsAndBudgets;
        var premiumActions = settings.PremiumExceedActions;
        var criticalActions = settings.PrivilegedExceedActions;

        Assert.IsTrue(settings.TryParse(
            "premiumlimitsandbudgets.hourlytokenlimit=2300,PRIVILEGEDEXCEEDACTIONS.HOURLYLIMITACTION=THROTTLE," +
            "DailyTokenLimit=42,DailyLimitAction=delay," +
            "ModelLimits=model%3Bone=DailyTokenLimit:77|DailyBudgetUsd:1.25," +
            "ModelLimitActions=model%3Bone=DailyLimitAction:requeue"));
        Assert.AreEqual(2300, settings.PremiumLimitsAndBudgets.HourlyTokenLimit);
        Assert.AreEqual(2200, settings.PremiumLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual(3100, settings.PrivilegedLimitsAndBudgets.HourlyTokenLimit);
        Assert.AreEqual(3200, settings.PrivilegedLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual(TokenActionEnum.Throttle, settings.PrivilegedExceedActions.HourlyLimitAction);
        Assert.AreEqual(TokenActionEnum.Bypass, settings.PrivilegedExceedActions.DailyLimitAction);
        Assert.AreEqual(TokenActionEnum.Reject, settings.PremiumExceedActions.DailyLimitAction);
        Assert.AreEqual(42, settings.DefaultLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual(TokenActionEnum.Delay, settings.DefaultExceededActions.DailyLimitAction);
        Assert.AreEqual(77, settings.ModelLimits["model;one"].DailyTokenLimit);
        Assert.AreEqual(1.25m, settings.ModelLimits["model;one"].DailyBudgetUsd);
        Assert.AreEqual(TokenActionEnum.Requeue, settings.ModelLimitActions["model;one"].DailyLimitAction);
        Assert.AreEqual(2100, premiumLimits.HourlyTokenLimit);
        Assert.AreEqual(3100, criticalLimits.HourlyTokenLimit);
        Assert.AreEqual(TokenActionEnum.Reject, premiumActions.DailyLimitAction);
        Assert.AreEqual(new ExceededActions().HourlyLimitAction, criticalActions.HourlyLimitAction);
        Assert.AreNotSame(premiumLimits, settings.PremiumLimitsAndBudgets);
        Assert.AreNotSame(criticalLimits, settings.PrivilegedLimitsAndBudgets);
        Assert.AreNotSame(premiumActions, settings.PremiumExceedActions);
        Assert.AreNotSame(criticalActions, settings.PrivilegedExceedActions);
        var roundTrip = new TokenomicsSettings();
        Assert.IsTrue(roundTrip.TryParse(settings.ToString()));
        Assert.AreEqual(settings.ToString(), roundTrip.ToString());
    }

    /// <summary>A rejected part cannot leak mutations from any workload container.</summary>
    [DataTestMethod]
    [DataRow("UnknownWorkloadLimits.HourlyTokenLimit=1")]
    [DataRow("PremiumLimitsAndBudgets.NotAField=1")]
    [DataRow("PrivilegedLimitsAndBudgets.HourlyTokenLimit=1.5")]
    [DataRow("PremiumLimitsAndBudgets.HourlyBudgetUsd=NaN")]
    [DataRow("PrivilegedExceedActions.DailyLimitAction=notAnAction")]
    [DataRow("PremiumExceedActions.DailyLimitAction=999")]
    [DataRow("PremiumExceedActions.DailyLimitAction=1")]
    [DataRow("PrivilegedExceedActions.HourlyTokenLimit=1")]
    [DataRow("PrivilegedLimitsAndBudgets.DailyLimitAction=bypass")]
    [DataRow("PrivilegedExceedActions.=bypass")]
    [DataRow("PrivilegedExceedActions.Nested.DailyLimitAction=bypass")]
    [DataRow("PrivilegedExceedActions=bypass")]
    [DataRow("HourlyQuotaGovernanceAction=bypass")]
    [DataRow("DailyBudgetEntitledTenantAction=bypass")]
    public void Settings_InvalidWorkloadPart_DoesNotMutateSettings(string invalidPart) {
        var settings = new TokenomicsSettings();
        Assert.IsTrue(settings.TryParse(
            "DailyTokenLimit=99;PremiumLimitsAndBudgets.DailyTokenLimit=1100;" +
            "PrivilegedLimitsAndBudgets.DailyTokenLimit=2200;" +
            "PremiumExceedActions.DailyLimitAction=reject;" +
            "PrivilegedExceedActions.DailyLimitAction=bypass"));
        var before = settings.ToString();
        var premiumLimits = settings.PremiumLimitsAndBudgets;
        var criticalLimits = settings.PrivilegedLimitsAndBudgets;
        var premiumActions = settings.PremiumExceedActions;
        var criticalActions = settings.PrivilegedExceedActions;
        Assert.IsTrue(settings.TryParse(invalidPart));
        CollectionAssert.AreEqual(new[] { invalidPart.Split('=', 2)[0] }, settings.IgnoredFields.ToArray());
        Assert.AreEqual(before, settings.ToString());
        Assert.AreSame(premiumLimits, settings.PremiumLimitsAndBudgets);
        Assert.AreSame(criticalLimits, settings.PrivilegedLimitsAndBudgets);
        Assert.AreSame(premiumActions, settings.PremiumExceedActions);
        Assert.AreSame(criticalActions, settings.PrivilegedExceedActions);
    }

    /// <summary>Existing flat settings load recognized values without restoring retired properties.</summary>
    [TestMethod]
    public void Settings_ExistingFlatOptions_LoadWithIgnoredFieldWarnings() {
        const string raw = """
            MonthlyTokenLimit=10000; DailyTokenLimit=1000; HourlyTokenLimit=100; HourlyBudgetUsd=10; DailyBudgetUsd=50; MonthlyBudgetUsd=1000; HourlyQuotaGovernanceAction=bypass; DailyQuotaGovernanceAction=increaseLimit; MonthlyQuotaGovernanceAction=reject; HourlyQuotaExceededAction=downgradeModel; DailyQuotaExceededAction=downgradeModel; MonthlyQuotaExceededAction=reject; HourlyBudgetExceededAction=downgradeModel; DailyBudgetExceededAction=downgradeModel; MonthlyBudgetExceededAction=downgradeModel; HourlyBudgetEntitledTenantAction=bypass; DailyBudgetEntitledTenantAction=bypass; MonthlyBudgetEntitledTenantAction=downgradeModel; DelayDuration=10:000; CurrentCapacityUtilizationPercent=0; CapacityConstraintThresholdPercent=80; HighQueueDepthThreshold=0.9; LargeContextThreshold=2048; CriticalPriority=1; HighPriority=2; DefaultTenant=Contoso; DefaultModel=gpt-4o; DefaultModelAPI=; ModelCostPerToken.gpt-4o=Input:0.23|CachedInput:0.23|Output:0.23; ModelCostPerToken.gpt-4.1=Input:0.23|CachedInput:0.23|Output:0.23; ModelCostPerToken.gpt-6-sol=Input:0.46|CachedInput:0.46|Output:0.46; ModelHierarchy=gpt: [gpt-6-sol, gpt-4o, gpt-4.1]; ModelHostOptions=gpt-4o=Host_apim=Enabled:true|Api:OpenAI%20Chat%20Completions:false:true:%2Fcl%2Fopenai%2Fv1%2Fchat%2Fcompletions|Api:OpenAI%20Responses:false:false; ModelHostOptions=gpt-6.1-sol=Host_6sol=Enabled:true|Api:OpenAI%20Chat%20Completions:false:false|Api:OpenAI%20Responses:false:false; ModelHostOptions=gpt-6-sol=Host_6sol=Enabled:true|Api:OpenAI%20Chat%20Completions:false:false|Api:OpenAI%20Responses:false:false; ModelHostOptions=gpt-6-sol=Host_apim=Enabled:true|Api:OpenAI%20Chat%20Completions:false:true:%2Fcl%2Fopenai%2Fv1%2Fchat%2Fcompletions|Api:OpenAI%20Responses:false:true:%2Fcl%2Fopenai%2Fv1%2Fchat%2Fcompletions; AbuseDetectedAction=reject; AdministratorOverrideAction=bypass; CapacityCriticalWorkloadAction=increasePriority; CapacityHighPriorityAction=requeue; CapacityConstrainedAction=delay; QueueCriticalPriorityAction=increasePriority; QueueDepthHighAction=requeue; PreferredModelReplacementAction=none; PreferredModelUnavailableAction=reject; LargeContextCapacityAvailableAction=upgradeModel; LargeContextRequestAction=changeModel; CriticalPriorityAction=increasePriority; HighPriorityEntitledTenantAction=increasePriority; TenantEntitlementAction=increaseLimit; GovernanceWorkloadAction=bypass; DefaultAction=none
            """;
        var settings = new TokenomicsSettings();
        Assert.IsTrue(settings.TryParse(raw));
        CollectionAssert.AreEqual(new[] {
            "HourlyQuotaGovernanceAction", "DailyQuotaGovernanceAction", "MonthlyQuotaGovernanceAction",
            "HourlyQuotaExceededAction", "DailyQuotaExceededAction", "MonthlyQuotaExceededAction",
            "HourlyBudgetEntitledTenantAction", "DailyBudgetEntitledTenantAction", "MonthlyBudgetEntitledTenantAction"
        }, settings.IgnoredFields.ToArray());
        Assert.AreEqual(100, settings.DefaultLimitsAndBudgets.HourlyTokenLimit);
        Assert.AreEqual(1000, settings.DefaultLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual(10000, settings.DefaultLimitsAndBudgets.MonthlyTokenLimit);
        Assert.AreEqual(50m, settings.DefaultLimitsAndBudgets.DailyBudgetUsd);
        Assert.AreEqual(TokenActionEnum.DowngradeModel, settings.DefaultExceededActions.HourlyBudgetExceededAction);
        Assert.AreEqual(TokenActionEnum.DowngradeModel, settings.PrivilegedExceedActions.HourlyLimitAction);
        Assert.AreEqual(10000, settings.DelayDuration);
        Assert.AreEqual("Contoso", settings.DefaultTenant);
        Assert.AreEqual("gpt-4o", settings.DefaultModel);
        Assert.AreEqual(string.Empty, settings.DefaultModelAPI);
        Assert.AreEqual(3, settings.ModelCostPerToken.Count);
        Assert.AreEqual(0.46m, settings.ModelCostPerToken["gpt-6-sol"].Output);
        CollectionAssert.AreEqual(new[] { "gpt-6-sol", "gpt-4o", "gpt-4.1" }, settings.ModelHierarchy["gpt"]);
        Assert.AreEqual(3, settings.ModelHostOptions.Count);
        Assert.AreEqual(4, settings.ModelHostOptions.Values.Sum(hosts => hosts.Count));
        Assert.AreEqual("/cl/openai/v1/chat/completions",
            settings.ModelHostOptions["gpt-6-sol"]["Host_apim"].APIs["OpenAI Responses"].Path);
        Assert.AreEqual(TokenActionEnum.None, settings.DefaultAction);
        var canonical = settings.ToString();
        Assert.IsFalse(canonical.Contains("IgnoredFields", StringComparison.Ordinal));
        Assert.IsFalse(canonical.Contains("QuotaGovernanceAction", StringComparison.Ordinal));
        var roundTrip = new TokenomicsSettings();
        Assert.IsTrue(roundTrip.TryParse(canonical));
        Assert.AreEqual(0, roundTrip.IgnoredFields.Count);
        Assert.AreEqual(canonical, roundTrip.ToString());
    }

    /// <summary>Invalid compound entries roll back only themselves and warnings clear after a clean parse.</summary>
    [TestMethod]
    public void Settings_IgnoredEntries_DoNotLeakPartialChangesOrSerializeDiagnostics() {
        var settings = new TokenomicsSettings();
        settings.ModelLimits["one"] = new LimitsAndBudgets { DailyTokenLimit = 77 };
        Assert.IsTrue(settings.TryParse(
            "ModelLimits=one=DailyTokenLimit:99|NotAField:1;" +
            "ModelLimits=two=DailyTokenLimit:99|NotAField:1;" +
            "DailyTokenLimit=not-a-number;InvalidValue=secret-value;broken-entry;" +
            "dailyTokenLimit=still-invalid;DefaultModel=kept"));
        Assert.AreEqual(77, settings.ModelLimits["one"].DailyTokenLimit);
        Assert.IsFalse(settings.ModelLimits.ContainsKey("two"));
        Assert.AreEqual(new LimitsAndBudgets().DailyTokenLimit, settings.DefaultLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual("kept", settings.DefaultModel);
        CollectionAssert.AreEqual(new[] {
            "ModelLimits", "DailyTokenLimit", "InvalidValue", "(malformed entry)"
        }, settings.IgnoredFields.ToArray());
        Assert.IsFalse(string.Join(", ", settings.IgnoredFields).Contains("secret-value", StringComparison.Ordinal));
        Assert.IsFalse(settings.ToString().Contains("IgnoredFields", StringComparison.Ordinal));
        Assert.IsTrue(settings.TryParse("DailyTokenLimit=250"));
        Assert.AreEqual(250, settings.DefaultLimitsAndBudgets.DailyTokenLimit);
        Assert.AreEqual(0, settings.IgnoredFields.Count);
    }

    [TestMethod]
    public void Settings_NestedFormats_RoundTrip() {
        var source = new TokenomicsSettings {
            DelayDuration = 1250,
            ModelCostPerToken = {
                ["gpt-4o"] = new ModelTokenPricing { Input = 1.1m, CachedInput = 0.2m, Output = 3.3m }
            },
            ModelHierarchy = {
                ["gpt"] = ["gpt-4o", "gpt-4o-mini"]
            }
        };

        var parsed = new TokenomicsSettings();
        Assert.IsTrue(parsed.TryParse(source.ToString()));
        Assert.AreEqual(1250, parsed.DelayDuration);
        Assert.AreEqual(1.1m, parsed.ModelCostPerToken["gpt-4o"].Input);
        Assert.AreEqual(0.2m, parsed.ModelCostPerToken["gpt-4o"].CachedInput);
        Assert.AreEqual(3.3m, parsed.ModelCostPerToken["gpt-4o"].Output);
        CollectionAssert.AreEqual(
            new[] { "gpt-4o", "gpt-4o-mini" },
            parsed.ModelHierarchy["gpt"]);
    }

    [TestMethod]
    public void Settings_DelayDuration_ParsesSuffixesAndClock() {
        var milliseconds = new TokenomicsSettings();
        Assert.IsTrue(milliseconds.TryParse("DelayDuration=1500ms"));
        Assert.AreEqual(1500, milliseconds.DelayDuration);

        var seconds = new TokenomicsSettings();
        Assert.IsTrue(seconds.TryParse("DelayDuration=2s"));
        Assert.AreEqual(2000, seconds.DelayDuration);

        var clock = new TokenomicsSettings();
        Assert.IsTrue(clock.TryParse("DelayDuration=01:02:003"));
        Assert.AreEqual(62003, clock.DelayDuration);
    }

    [TestMethod]
    public void Settings_LegacyModelCost_TreatsScalarAsOutputPrice() {
        var parsed = new TokenomicsSettings();
        Assert.IsTrue(parsed.TryParse("ModelCostPerToken.gpt-4o=5"));
        Assert.AreEqual(0m, parsed.ModelCostPerToken["gpt-4o"].Input);
        Assert.AreEqual(0m, parsed.ModelCostPerToken["gpt-4o"].CachedInput);
        Assert.AreEqual(5m, parsed.ModelCostPerToken["gpt-4o"].Output);
    }

    [TestMethod]
    public async Task LivePipeline_SkipsMalformedSummaries_AndAggregatesValidRecordsAcrossBatches() {
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var live = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse([]), store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        using var reader = new EventHubReader(new EventHubMonitorStore(), new ProxyMetricsCatalog(),
            Options.Create(new EventHubMonitorOptions()), NullLogger<EventHubReader>.Instance, live);
        Assert.IsTrue(store.GetSnapshot().IsSampleData, "Unconfigured readers must not disable sample data.");
        live.BeginLive();
        await live.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        var initial = store.GetSnapshot();
        Assert.IsFalse(initial.IsSampleData);
        Assert.AreEqual("0", Metric(store, "Total Requests"));
        var invalid = new[] {
            "not json", "[]",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"bad\",\"RecordKind\":\"unknown\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"RecordKind\":\"RequestOutcome\",\"Status\":\"200\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"bad\",\"SchemaVersion\":\"1\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"bad\",\"RecordKind\":\"PolicyDecision\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"bad\",\"MID\":\"duplicate\",\"RecordKind\":\"PolicyDecision\",\"PolicyAction\":\"Throttle\"}",
            Event("bad", "RequestOutcome", input: "-1"), Event("bad", "RequestOutcome", cached: "101"),
            Event("bad", "RequestOutcome", status: "OK"), Event("bad", "RequestOutcome", timestamp: "bad"),
            Event("bad", "RequestOutcome").Replace("\"RecordKind\"", "\"recordKind\"", StringComparison.Ordinal),
            Event("bad", "RequestOutcome").Replace("\"Type\"", "\"TYPE\"", StringComparison.Ordinal),
            Event("bad", "RequestOutcome").Replace("\"UserId\":\"user\"", "\"UserId\":{}", StringComparison.Ordinal)
        };
        var pipeline = typeof(EventHubReader).GetMethod("RunPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pipeline.Invoke(reader, [invalid, "test partition"]);
        Assert.AreSame(initial, store.GetSnapshot());
        pipeline.Invoke(reader, [new[] {
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"ordinary\",\"Status\":\"403\"}",
            "{\"Type\":\"other\",\"MID\":\"unrelated\"}",
            Event("same", "PolicyDecision", action: "Throttle"),
            Event("same", "RequestOutcome", status: "429")
        }, "test partition 0"]);
        Assert.AreSame(initial, store.GetSnapshot(), "Live ingestion must not rebuild snapshots for each event.");
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "1");
        var first = store.GetSnapshot();
        Assert.AreEqual("1", Metric(store, "Total Requests"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual("0.0%", Metric(store, "Success Rate"));
        pipeline.Invoke(reader, [new[] {
            Event("same", "PolicyDecision", action: "Throttle"),
            Event("same", "RequestOutcome", status: "429"),
            Event("successful", "RequestOutcome", model: "requested", effectiveModel: "effective")
        }, "test partition 1"]);
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "2");
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("2", Metric(store, "Total Requests"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual("50.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("240", snapshot.TotalTokens);
        Assert.AreEqual(150.0, snapshot.Trend.Sum(point => point.InputNet));
        Assert.AreEqual(50.0, snapshot.Trend.Sum(point => point.Cached));
        Assert.AreEqual(40.0, snapshot.Trend.Sum(point => point.Output));
        Assert.AreEqual("120", snapshot.Models.Single(model => model.Name == "effective").Tokens);
        Assert.AreEqual("0", snapshot.Models.Single(model => model.Name == "requested").Tokens);
        Assert.AreEqual("Unavailable", Metric(store, "Total Spend (USD)"));
        StringAssert.Contains(snapshot.SnapshotLabel, "Event Hub");
        CollectionAssert.AreEqual(new[] { "Retained events" }, snapshot.TimePeriods.ToArray());
        Assert.AreEqual("120", first.TotalTokens, "Published snapshots must remain immutable.");
        Assert.AreEqual("0", initial.TotalTokens);
        clock.AdvanceSecond();
        await Task.Delay(20);
        Assert.AreSame(snapshot, store.GetSnapshot(), "Idle ticks must not republish unchanged snapshots.");
        await live.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LiveReader_LocalImportFeedsTokenomicsWithLiveConsumptionDisabled() {
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var live = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse([]), store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        using var reader = new EventHubReader(new EventHubMonitorStore(), new ProxyMetricsCatalog(),
            Options.Create(new EventHubMonitorOptions {
                EventHubEnabled = false,
                LocalFilePath = WriteFile(Event("imported", "RequestOutcome") + "\n" + Event("imported", "PolicyDecision"))
            }), NullLogger<EventHubReader>.Instance, live);
        await live.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        await reader.StartAsync(CancellationToken.None);
        await reader.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await reader.StopAsync(CancellationToken.None);
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "1");
        Assert.IsFalse(store.GetSnapshot().IsSampleData);
        Assert.AreEqual("120", store.GetSnapshot().TotalTokens);
        Assert.AreEqual("1", Metric(store, "Total Requests"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        await live.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task LiveAggregation_SerializesConcurrentPartitions_AndBoundsRetention() {
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var live = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse([]), store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        live.BeginLive();
        await live.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        var options = TokenomicsReplayOptions.Parse(["--run", "events",
            WriteFile(string.Join("\n", Enumerable.Range(0, 10_001).Select(index =>
                Event($"request-{index}", "RequestOutcome", user: $"user-{index}"))))]);
        var records = options.Events.Select(fields => new ParsedEventRecord("", fields)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
            live.PublishRecords(records.Skip(index * 10).Take(10).ToArray()))));
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "100");
        Assert.AreEqual("100", Metric(store, "Total Requests"));
        Assert.AreEqual("12,000", store.GetSnapshot().TotalTokens);
        live.PublishRecords(records);
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "10,000");
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("10,000", Metric(store, "Total Requests"));
        Assert.AreEqual("1,200,000", snapshot.TotalTokens);
        Assert.AreEqual(10_000, snapshot.Users.Length);
        StringAssert.Contains(snapshot.SnapshotLabel, "latest 10,000 retained");
        live.PublishRecords([new ParsedEventRecord("", options.Events[0])]);
        clock.AdvanceSecond();
        await WaitUntil(() => !ReferenceEquals(snapshot, store.GetSnapshot()));
        Assert.AreEqual("10,000", Metric(store, "Total Requests"));
        Assert.AreEqual("1,200,000", store.GetSnapshot().TotalTokens);
        await live.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public void FileReplay_IgnoresLivePublication_AndKeepsItsOwnSnapshot() {
        using var store = new TokenomicsDashboardStore();
        var options = TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(Event("replay", "RequestOutcome"))]);
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance);
        var initial = store.GetSnapshot();
        replay.BeginLive();
        replay.PublishRecords([new ParsedEventRecord("", options.Events[0])]);
        Assert.AreSame(initial, store.GetSnapshot());
        StringAssert.Contains(initial.SnapshotLabel, "Local replay");
    }

    [TestMethod]
    public void FileValidation_RejectsMissingAndMalformedFilesBeforeAnyReplay() {
        var missing = Assert.ThrowsException<ArgumentException>(() =>
            TokenomicsReplayOptions.Parse(["--run", "events", Path.Combine(_directory, "missing.ndjson")]));
        StringAssert.Contains(missing.Message, "Cannot read replay file");
        foreach (var bad in new[] {
            "not json", "[]", "{}", "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"other\"}",
            Event("a", "RequestOutcome", input: "-1"), Event("a", "RequestOutcome", cached: "101"),
            Event("a", "RequestOutcome", status: "OK"), Event("a", "RequestOutcome", timestamp: "bad"),
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"PolicyDecision\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"SchemaVersion\":\"1\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"MID\":\"a\",\"RecordKind\":\"\"}",
            "{\"Type\":\"S7P-Tokenomics\",\"RecordKind\":\"RequestOutcome\",\"Status\":\"200\"}"
        }) {
            var file = WriteFile(Event("valid", "RequestOutcome") + "\n" + bad);
            var error = Assert.ThrowsException<FormatException>(() => TokenomicsReplayOptions.Parse(["--run", "events", file]));
            StringAssert.Contains(error.Message, "line 2");
            StringAssert.Contains(error.Message, file);
        }
    }

    [TestMethod]
    public async Task Startup_InvalidArgumentsAndFilesExitWithUsefulErrors() {
        foreach (var args in new[] {
            new[] { "--run", "events" },
            new[] { "--uionly", "--run", "events", Path.Combine(_directory, "missing.ndjson") },
            new[] { "--run", "events", WriteFile("broken json") }
        }) {
            var start = new ProcessStartInfo("dotnet") {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(TokenomicsEventReplay).Assembly.Location);
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(1, process.ExitCode);
            StringAssert.Contains((await error).ToLowerInvariant(), "replay");
            Assert.IsFalse((await output).Contains("Now listening", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task Startup_UiOnlyReplay_RendersFinalDataWithoutSyntheticOrInvalidQuotaWidths() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var assembly = typeof(TokenomicsEventReplay).Assembly.Location;
        var start = new ProcessStartInfo("dotnet") {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
        };
        foreach (var argument in new[] { assembly, "--uionly", "--run", "events",
            WriteFile(Event("rendered", "RequestOutcome")), "--urls", $"http://127.0.0.1:{port}" }) {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["CompanionApp__History__Mode"] = "Disk";
        start.Environment["CompanionApp__Conversations__Mode"] = "Disk";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string html = "";
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(20) && !process.HasExited) {
                try {
                    html = await client.GetStringAsync($"http://127.0.0.1:{port}/tokenomics");
                    if (html.Contains("1/1 events", StringComparison.Ordinal)) break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
            StringAssert.Contains(html, "1/1 events");
            StringAssert.Contains(html, "EOF");
            StringAssert.Contains(html, "120");
            StringAssert.Contains(html, "no price data");
            Assert.IsFalse(html.Contains("Sample data", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("vs. previous period", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("width: Unavailable", StringComparison.Ordinal));
            Assert.IsFalse(html.Contains("Increasing usage trend", StringComparison.Ordinal));
        }
        finally {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        StringAssert.Contains(await output, "UI-only mode");
        Assert.IsFalse((await error).Contains("Unhandled exception", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Replay_PublishesTenEachSecond_StopsAtEof_AndRetainsImmutableFinalSnapshot() {
        var options = TokenomicsReplayOptions.Parse(["--run", "events",
            WriteFile(string.Join("\n", Enumerable.Range(0, 23).Select(index => Event($"request-{index}", "RequestOutcome"))))]);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        var initial = store.GetSnapshot();
        Assert.IsFalse(initial.IsSampleData);
        Assert.IsTrue(initial.Users.IsEmpty);
        Assert.IsTrue(initial.Trend.IsEmpty);
        Assert.IsTrue(initial.Models.IsEmpty);
        Assert.IsTrue(initial.Reports.IsEmpty);
        Assert.IsTrue(initial.TenantSpend.IsEmpty);
        Assert.IsTrue(initial.Quotas.IsEmpty);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        Assert.AreSame(initial, store.GetSnapshot(), "No events before the first one-second tick.");
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "10");
        var first = store.GetSnapshot();
        Assert.AreEqual("1,200", first.TotalTokens);
        clock.AdvanceSecond();
        await WaitUntil(() => Metric(store, "Total Requests") == "20");
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var final = store.GetSnapshot();
        StringAssert.Contains(final.SnapshotLabel, "23/23 events · EOF");
        Assert.AreEqual("2,760", final.TotalTokens);
        Assert.AreEqual("10", first.Metrics.Single(metric => metric.Name == "Total Requests").Value);
        Assert.AreEqual(0, initial.Trend.Length);
        clock.AdvanceSecond();
        await Task.Delay(1100);
        Assert.AreSame(final, store.GetSnapshot(), "EOF must neither loop nor restart synthetic updates.");
        await replay.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task OrdinaryTokenomicsLogs_ConsumeCadenceWithoutContributingToAggregation() {
        var ordinary = JsonSerializer.Serialize(new Dictionary<string, string> {
            ["Type"] = "S7P-Tokenomics", ["MID"] = "rejected", ["Status"] = "403",
            ["Error"] = "Request Rejected", ["UserId"] = "ordinary-user", ["Tenant"] = "ordinary-tenant",
            ["RequestedModel"] = "ordinary-model", ["InputTokens"] = "999", ["TimestampUtc"] = "not-a-summary-timestamp"
        });
        var records = Enumerable.Repeat(ordinary, 9)
            .Append("{\"Type\":\"S7P-Tokenomics\",\"Status\":\"403\",\"Error\":\"Request Rejected\"}")
            .Append(Event("outcome", "RequestOutcome"))
            .Append(Event("decision", "PolicyDecision"));
        var options = TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]);
        Assert.AreEqual(12, options.Events.Length);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await WaitUntil(() => store.GetSnapshot().SnapshotLabel.Contains("10/12 events", StringComparison.Ordinal));
        var first = store.GetSnapshot();
        Assert.AreEqual("0", first.TotalTokens);
        Assert.AreEqual("0", Metric(store, "Total Requests"));
        Assert.AreEqual("0", Metric(store, "Policy Actions"));
        Assert.IsTrue(first.Users.IsEmpty && first.Models.IsEmpty && first.Trend.IsEmpty);
        Assert.IsFalse(replay.ExecuteTask!.IsCompleted);
        clock.AdvanceSecond();
        await replay.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(5));
        var final = store.GetSnapshot();
        StringAssert.Contains(final.SnapshotLabel, "12/12 events · EOF");
        Assert.AreEqual("120", final.TotalTokens);
        Assert.AreEqual("2", Metric(store, "Total Requests"));
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual(1, final.Users.Length);
        Assert.AreEqual(1, final.Models.Length);
        Assert.IsFalse(final.Tenants.Contains("ordinary-tenant"));
    }

    [TestMethod]
    public async Task Cancellation_BeforeFirstTickAndBetweenBatches_IsNotFaulted() {
        foreach (var submitFirstBatch in new[] { false, true }) {
            var options = TokenomicsReplayOptions.Parse(["--run", "events",
                WriteFile(string.Join("\n", Enumerable.Range(0, 15).Select(index => Event(index.ToString(), "RequestOutcome"))))]);
            using var store = new TokenomicsDashboardStore();
            var clock = new ReplayClock();
            using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
            await replay.StartAsync(CancellationToken.None);
            await WaitUntil(() => clock.HasTimer);
            if (submitFirstBatch) {
                clock.AdvanceSecond();
                await WaitUntil(() => Metric(store, "Total Requests") == "10");
            }
            var last = store.GetSnapshot();
            await replay.StopAsync(CancellationToken.None);
            Assert.IsTrue(replay.ExecuteTask!.IsCompletedSuccessfully);
            clock.AdvanceSecond();
            Assert.AreSame(last, store.GetSnapshot());
            Assert.IsFalse(last.IsSampleData);
        }
    }

    [TestMethod]
    public async Task ProductionSample_950OutcomesAnd50Decisions_HasNoInventedUsageOrPrices() {
        string[] models = ["gpt-4o", "gpt-4o-mini", "gpt-5", "gpt-5-mini"];
        var actions = Enum.GetValues<TokenActionEnum>();
        var records = Enumerable.Range(0, 1000).Select(index => {
            var summary = new TokenomicsSummaryEvent($"request-{index}", 1, $"user-{index % 20}",
                $"tenant-{index % 4}", models[index % 4], models[index % 4], 2, "sample", actions[index % actions.Length]);
            if (index < 950) {
                summary.PrepForFinalStats(HttpStatusCode.OK, TimeSpan.FromMilliseconds(100), "backend",
                    models[index % 4], 1, 1, new LLMStats { InputTokens = 100, CachedTokens = 25, OutputTokens = 20 });
            }
            else if (actions[index % actions.Length] == TokenActionEnum.Throttle) {
                summary.SetLocalStatus(429);
            }
            return ProxyEvent.ConvertToJson(summary, new Dictionary<string, string> {
                ["MID"] = summary.MID!, ["Type"] = "S7P-Tokenomics",
                ["Status"] = ((int)summary.Status).ToString(CultureInfo.InvariantCulture)
            });
        }).ToArray();
        var options = TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]);
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(options, store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        for (var batch = 1; batch <= 100; batch++) {
            clock.AdvanceSecond();
            await WaitUntil(() => store.GetSnapshot().SnapshotLabel.Contains($"{batch * 10}/1000 events", StringComparison.Ordinal));
        }
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("114,000", snapshot.TotalTokens);
        Assert.AreEqual("1,000", Metric(store, "Total Requests"));
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.AreEqual(Enumerable.Range(950, 50).Count(index =>
            actions[index % actions.Length] is not (TokenActionEnum.Bypass or TokenActionEnum.None)).ToString(),
            Metric(store, "Policy Actions"));
        Assert.AreEqual(Enumerable.Range(950, 50).Count(index => actions[index % actions.Length] == TokenActionEnum.Throttle).ToString(), Metric(store, "429 Throttles"));
        Assert.AreEqual(20, snapshot.Users.Length);
        Assert.AreEqual(5, snapshot.Tenants.Length);
        Assert.AreEqual(4, snapshot.Models.Length);
        Assert.AreEqual(950 * 75, snapshot.Trend.Sum(point => point.InputNet));
        Assert.AreEqual(950 * 25, snapshot.Trend.Sum(point => point.Cached));
        Assert.AreEqual(950 * 20, snapshot.Trend.Sum(point => point.Output));
        Assert.IsTrue(snapshot.Metrics.All(metric => metric.Change == ""));
        Assert.AreEqual("Unavailable", Metric(store, "Total Spend (USD)"));
        Assert.IsTrue(snapshot.Users.All(user => user.Spend == "Unavailable" && user.Quota == "Unavailable" && user.Sparkline == ""));
        Assert.IsTrue(snapshot.SpendLine.IsEmpty);
        Assert.IsTrue(snapshot.SpendAxisTicks.IsEmpty);
        Assert.IsTrue(snapshot.TenantSpend.IsEmpty);
        Assert.IsTrue(snapshot.Quotas.IsEmpty);
        Assert.IsFalse(snapshot.ModelGradient.Contains("NaN", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ModelSubstitution_ShowsRequestedAndEffectiveModels_AndZeroUsageIsSafe() {
        var records = new[] {
            Event("routed", "RequestOutcome", model: "requested", effectiveModel: "effective"),
            Event("zero", "RequestOutcome", input: "0", cached: "0", model: "zero-model", output: "0")
        };
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]),
            store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("0", snapshot.Models.Single(model => model.Name == "requested").Tokens);
        Assert.AreEqual("120", snapshot.Models.Single(model => model.Name == "effective").Tokens);
        Assert.AreEqual("0.0%", snapshot.Models.Single(model => model.Name == "zero-model").Share);
        Assert.AreEqual("100.0%", Metric(store, "Success Rate"));
        Assert.IsTrue(snapshot.TokenAxisMaximum > 0);
    }

    [TestMethod]
    public async Task DecisionsAndOutcomes_SharingMid_DoNotDoubleCountRequestsOrThrottles() {
        var records = new[] {
            Event("same", "PolicyDecision", action: "Throttle"),
            Event("same", "RequestOutcome", status: "429"),
            Event("same", "RequestOutcome", status: "429"),
            Event("decision-only", "PolicyDecision", action: "Bypass", user: "only-user", tenant: "only-tenant", model: "only-model"),
            Event("decision-only", "RequestOutcome", status: "200", action: "Bypass", user: "only-user", tenant: "only-tenant", model: "only-model")
        };
        using var store = new TokenomicsDashboardStore();
        var clock = new ReplayClock();
        using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(string.Join("\n", records))]),
            store, NullLogger<TokenomicsEventReplay>.Instance, clock);
        await replay.StartAsync(CancellationToken.None);
        await WaitUntil(() => clock.HasTimer);
        clock.AdvanceSecond();
        await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = store.GetSnapshot();
        Assert.AreEqual("2", Metric(store, "Total Requests"));
        Assert.AreEqual("50.0%", Metric(store, "Success Rate"));
        Assert.AreEqual("1", Metric(store, "Policy Actions"));
        Assert.AreEqual("1", Metric(store, "429 Throttles"));
        Assert.AreEqual("120", snapshot.TotalTokens);
        Assert.IsTrue(snapshot.Users.Any(user => user.Name == "only-user" && user.Total == "0"));
        Assert.IsTrue(snapshot.Tenants.Contains("only-tenant"));
        Assert.IsTrue(snapshot.Models.Any(model => model.Name == "only-model" && model.Share == "0.0%"));
    }

    [TestMethod]
    public async Task EmptyAndDecisionOnlyReplay_HavePositiveAxisAndNoSampleFallback() {
        foreach (var content in new[] { "", Event("only", "PolicyDecision"), Event("zero", "RequestOutcome", input: "0", cached: "0", output: "0") }) {
            using var store = new TokenomicsDashboardStore();
            var clock = new ReplayClock();
            using var replay = new TokenomicsEventReplay(TokenomicsReplayOptions.Parse(["--run", "events", WriteFile(content)]),
                store, NullLogger<TokenomicsEventReplay>.Instance, clock);
            await replay.StartAsync(CancellationToken.None);
            if (content.Length > 0) {
                await WaitUntil(() => clock.HasTimer);
                clock.AdvanceSecond();
            }
            await replay.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = store.GetSnapshot();
            Assert.AreEqual("0", snapshot.TotalTokens);
            Assert.IsTrue(snapshot.TokenAxisMaximum > 0 && double.IsFinite(snapshot.TokenAxisMaximum));
            Assert.AreEqual(content.Contains("RequestOutcome", StringComparison.Ordinal) ? "100.0%" : "Unavailable", Metric(store, "Success Rate"));
            Assert.IsFalse(snapshot.IsSampleData);
            Assert.IsTrue(snapshot.Trend.All(point => point.InputNet + point.Cached + point.Output == 0));
            Assert.IsTrue(snapshot.Reports.IsEmpty);
            Assert.IsFalse(snapshot.ModelGradient.Contains("NaN", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task NormalStore_ContinuesOneSecondSyntheticUpdates() {
        using var store = new TokenomicsDashboardStore();
        var initial = store.GetSnapshot();
        await WaitUntil(() => !ReferenceEquals(initial, store.GetSnapshot()));
        Assert.IsTrue(store.GetSnapshot().IsSampleData);
        Assert.IsFalse(store.GetSnapshot().Users.IsEmpty);
    }

    private string WriteFile(string content) {
        var file = Path.Combine(_directory, $"{Guid.NewGuid():N}.ndjson");
        File.WriteAllText(file, content);
        return file;
    }

    private static string Metric(TokenomicsDashboardStore store, string name) =>
        store.GetSnapshot().Metrics.Single(metric => metric.Name == name).Value;

    private static string Event(string mid, string kind, string status = "200", string input = "100", string cached = "25",
        string action = "Throttle", string timestamp = "2026-01-01T00:00:00Z", string user = "user", string tenant = "tenant", string model = "model", string output = "20", string effectiveModel = "") =>
        JsonSerializer.Serialize(new Dictionary<string, string> {
            ["Type"] = "S7P-Tokenomics", ["MID"] = mid, ["RecordKind"] = kind,
            ["StatusCode"] = status, ["Status"] = status, ["InputTokens"] = input, ["CachedTokens"] = cached,
            ["OutputTokens"] = output, ["PolicyAction"] = action, ["TimestampUtc"] = timestamp,
            ["UserId"] = user, ["Tenant"] = tenant, ["RequestedModel"] = model, ["EffectiveModel"] = effectiveModel
        });

    private static async Task WaitUntil(Func<bool> condition) {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.IsTrue(condition(), "Timed out waiting for replay state.");
    }

    private sealed class ReplayClock : TimeProvider {
        private ReplayTimer? _timer;
        public bool HasTimer => Volatile.Read(ref _timer) is { Active: true };
        public void AdvanceSecond() => Volatile.Read(ref _timer)?.Tick();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            var timer = new ReplayTimer(callback, state);
            Volatile.Write(ref _timer, timer);
            return timer;
        }

        private sealed class ReplayTimer(TimerCallback callback, object? state) : ITimer {
            private int _active = 1;
            public bool Active => Volatile.Read(ref _active) == 1;
            public void Tick() { if (Active) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Active;
            public void Dispose() => Interlocked.Exchange(ref _active, 0);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
