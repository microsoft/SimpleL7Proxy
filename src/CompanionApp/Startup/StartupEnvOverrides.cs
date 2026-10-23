using System.Text;
using CompanionApp.Components.Shared;
using SimpleL7Proxy.Tokenomics;

namespace CompanionApp.Startup;

internal static class StartupEnvOverrides
{
    internal static List<string> ApplyStartupOverrides(
        IReadOnlyList<AppConfigurationScaffoldSetting> drafts,
        string appConfigurationEndpoint)
    {
        var overrides = new List<string>();
        var checkpoints = new StringBuilder("ApplyStartupOverrides: entered");
        var stage = "entered";
        try
        {
            var metricsServerOverride = Environment.GetEnvironmentVariable("MetricsServerOverride");
            if (metricsServerOverride is not null)
            {
                drafts.Single(setting => setting.Key == "Warm:Tokenomics:MetricsServer")
                    .DraftValue = metricsServerOverride;
                drafts.Single(setting => setting.Key == "Warm:Tokenomics:Enable")
                    .DraftValue = "true";
                drafts.Single(setting => setting.Key == "Warm:Tokenomics:Options")
                    .DraftValue = new TokenomicsSettings().ToString();
                overrides.Add("Warm:Tokenomics:MetricsServer");
                overrides.Add("Warm:Tokenomics:Enable");
                overrides.Add("Warm:Tokenomics:Options");
                checkpoints.Append("; MetricsServer override applied");
                stage = "MetricsServer override checked";
            }
            else
            {
                checkpoints.Append("; MetricsServer override not set");
                stage = "MetricsServer override checked";
            }

            var appInsightsConnectionStringOverride =
                Environment.GetEnvironmentVariable("AppInsightsConnectionStringOverride");
            if (appInsightsConnectionStringOverride is not null)
            {
                drafts.Single(setting => setting.Key == "Cold:Logging:AppInsightsConnectionString")
                    .DraftValue = appInsightsConnectionStringOverride;
                overrides.Add("Cold:Logging:AppInsightsConnectionString");
                checkpoints.Append("; AppInsights override applied");
                stage = "AppInsights override checked";
            }
            else
            {
                checkpoints.Append("; AppInsights override not set");
                stage = "AppInsights override checked";
            }

            var sidecarOverride = Environment.GetEnvironmentVariable("SidecarOverride");
            if (sidecarOverride is not null)
            {
                drafts.Single(setting => setting.Key == "Warm:HealthProbe:Sidecar")
                    .DraftValue = $"Enabled=true;url={sidecarOverride}";
                overrides.Add("Warm:HealthProbe:Sidecar");
                checkpoints.Append("; Sidecar override applied");
                stage = "Sidecar override checked";
            }
            else
            {
                checkpoints.Append("; Sidecar override not set");
                stage = "Sidecar override checked";
            }

            var profileStorageAccountName = Environment.GetEnvironmentVariable("CompanionApp__Profiles__StorageAccountName");
            if (!string.IsNullOrWhiteSpace(profileStorageAccountName)
                && !string.IsNullOrWhiteSpace(appConfigurationEndpoint)
                && Uri.TryCreate(appConfigurationEndpoint, UriKind.Absolute, out var appConfigurationUri))
            {
                // Accepts a URL (https://myaccount.blob.core.windows.net/) or a plain account name (simplel7proxystorage1658).
                var account = Uri.TryCreate(profileStorageAccountName, UriKind.Absolute, out var profileStorageUri)
                    ? profileStorageUri.Host.Split('.')[0]
                    : profileStorageAccountName.Trim().Split('.')[0];
                checkpoints.Append($"; profile storage account={account}");

                var profileUrlDefaults = new Dictionary<string, string>
                {
                    ["Warm:Profiles:SuspendedUser:ConfigUrl"] = $"azblob://{account}/profiles/suspended.json",
                    ["Warm:Profiles:User:UserConfigUrl"] = $"azblob://{account}/profiles/config.json",
                    ["Warm:Profiles:Auth:ConfigUrl"] = $"azblob://{account}/profiles/appids.json"
                };
                foreach (var (key, defaultUrl) in profileUrlDefaults)
                {
                    var draft = drafts.Single(setting => setting.Key == key);
                    if (string.IsNullOrWhiteSpace(draft.DraftValue))
                    {
                        draft.DraftValue = defaultUrl;
                        overrides.Add(key);
                        checkpoints.Append($"; {key} defaulted");
                        stage = $"{key} defaulted";
                    }
                    else
                    {
                        checkpoints.Append($"; {key} already set");
                        stage = $"{key} already set";
                    }
                }
            }
            else
            {
                checkpoints.Append("; profile URL defaults skipped (env var, endpoint, or parse)");
                stage = "profile URL defaults skipped";
            }
        }
        finally
        {
            if (Program.DEBUGLEVEL >= 2)
            {
                Console.WriteLine(checkpoints.ToString());
            }
            else if (Program.DEBUGLEVEL == 1)
            {
                Console.WriteLine($"ApplyStartupOverrides: {stage}");
            }
        }

        return overrides;
    }
}
