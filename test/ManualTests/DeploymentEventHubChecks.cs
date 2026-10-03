#:project ../../src/CompanionApp/CompanionApp.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanionApp.Components.Pages;

var root = Directory.GetCurrentDirectory();
var values = File.ReadLines(Path.Combine(root, "deployment/interactive/deploy.parameters.example.sh"))
    .Where(line => line.StartsWith("export ", StringComparison.Ordinal))
    .Select(line => Regex.Match(line, "^export ([A-Z][A-Z0-9_]*)=(?:\"([^\"]*)\"|([^ #]+))"))
    .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var setup = new DeploymentSetupPage();
typeof(DeploymentSetupPage).GetMethod("OnInitialized", flags)!.Invoke(setup, null);
var fields = ((System.Collections.IEnumerable)typeof(DeploymentSetupPage).GetField("_fields", flags)!.GetValue(setup)!)
    .Cast<object>().ToDictionary(field => (string)field.GetType().GetProperty("Key")!.GetValue(field)!);
var errors = (Dictionary<string, string>)typeof(DeploymentSetupPage).GetField("_errors", flags)!.GetValue(setup)!;
var review = typeof(DeploymentSetupPage).GetMethod("Review", flags)!;
var setValue = typeof(DeploymentSetupPage).GetMethod("SetValue", flags)!;
string[] keys = ["COMPANION_EVENTHUB_NAMESPACE", "COMPANION_EVENTHUB_NAME", "COMPANION_EVENTHUB_CONSUMER_GROUP"];
foreach (var key in keys) {
    Check((string)fields[key].GetType().GetProperty("Group")!.GetValue(fields[key])! == "companion", key + " belongs to the Companion App panel");
    var original = (string)fields[key].GetType().GetProperty("Value")!.GetValue(fields[key])!;
    foreach (var invalid in new[] { "", "invalid/name", new string('a', 257) }) {
        setValue.Invoke(setup, [fields[key], invalid]);
        review.Invoke(setup, ["companion"]);
        Check(errors.ContainsKey(key), key + " rejects invalid Azure resource names");
    }
    setValue.Invoke(setup, [fields[key], original]);
}
setValue.Invoke(setup, [fields["COMPANION_EVENTHUB_CONSUMER_GROUP"], "$Default"]);
review.Invoke(setup, ["companion"]);
Check(errors.Count == 0, "the built-in $Default consumer group is accepted");
setValue.Invoke(setup, [fields["DEPLOY_COMPANION_APP"], "false"]);
setValue.Invoke(setup, [fields["COMPANION_EVENTHUB_NAMESPACE"], ""]);
review.Invoke(setup, ["companion"]);
Check(errors.Count == 0, "disabled Companion App skips Event Hub validation");

values["PRIVATE_NETWORK_DEPLOYMENT"] = "no";
values["ASYNC_DEPLOYMENT"] = "no";
values["HEALTHPROBE_TYPE"] = "internal";
foreach (var enabled in new[] { false, true })
foreach (var shared in new[] { false, true })
foreach (var consumerGroup in new[] { "monitor-reader", "$Default" }) {
    values["DEPLOY_COMPANION_APP"] = enabled ? "true" : "false";
    values["COMPANION_APP_RESOURCE_GROUP"] = shared ? values["CONTAINER_APP_RESOURCE_GROUP"] : "rg-eventhub-companion";
    values["COMPANION_EVENTHUB_NAMESPACE"] = "deployment-check-events";
    values["COMPANION_EVENTHUB_NAME"] = "request.events";
    values["COMPANION_EVENTHUB_CONSUMER_GROUP"] = consumerGroup;
    var files = DeploymentBicepBundle.Generate(values);
    var settings = JsonNode.Parse(files["parameters.json"])!["parameters"]!["settings"]!["value"]!;
    foreach (var key in keys) Check(settings[key]!.GetValue<string>() == values[key], key + " survives parameter export");
    Check(settings["DEPLOY_COMPANION_APP"]!.GetValue<bool>() == enabled, "Companion App selection survives export");
    Check(settings["RESOURCE_GROUPS"]!.AsArray().Any(group => group!.GetValue<string>() == values["COMPANION_APP_RESOURCE_GROUP"]) == (enabled || shared), "Event Hub uses the selected Companion App resource group");
    foreach (var module in new[] { "eventHub", "eventHubAccess" }) {
        Check(Regex.IsMatch(files["main.bicep"], $@"module {module} .* = if \(settings.DEPLOY_COMPANION_APP\)"), module + " is conditional");
        Check(Regex.IsMatch(files["main.bicep"], $@"module {module} .*[\s\S]*?scope: resourceGroup\(settings.COMPANION_APP_RESOURCE_GROUP\)"), module + " uses the Companion App resource group");
    }
    var app = files["modules/companion-app.bicep"];
    foreach (var setting in new[] { "eventhub_enabled", "EventHubNamespace", "EventHubName", "ConsumerGroup" })
        Check(app.Contains($"name: 'CompanionApp__EventHubMonitor__{setting}'", StringComparison.Ordinal), "monitor receives " + setting);
    Check(app.Contains("param eventHubMonitorEnabled bool = false", StringComparison.Ordinal), "bootstrap does not connect before receiver RBAC");
    Check(app.Contains("value: string(eventHubMonitorEnabled)", StringComparison.Ordinal), "enablement is emitted as an environment string");
    Check(app.Contains("value: '${settings.COMPANION_EVENTHUB_NAMESPACE}.servicebus.windows.net'", StringComparison.Ordinal), "namespace is emitted as a fully qualified endpoint");
    Check(app.Contains("value: settings.COMPANION_EVENTHUB_NAME", StringComparison.Ordinal), "hub environment uses the selected name");
    Check(app.Contains("value: settings.COMPANION_EVENTHUB_CONSUMER_GROUP", StringComparison.Ordinal), "consumer environment uses the selected group");
    var finalApp = files["main.bicep"].Split("module companionApp '", StringSplitOptions.None)[1].Split("module metricsServer '", StringSplitOptions.None)[0];
    Check(finalApp.Contains("eventHubMonitorEnabled: true", StringComparison.Ordinal) && finalApp.Contains("    eventHubAccess\n", StringComparison.Ordinal), "final revision enables the monitor only after receiver access");
    var access = files["modules/event-hub-access.bicep"];
    Check(access.Contains("a638d3c7-ab3a-418d-83e6-5f17a39d4fde", StringComparison.Ordinal), "receiver uses the built-in role");
    Check(access.Contains("scope: eventHub", StringComparison.Ordinal) && access.Contains("principalId: companionAppPrincipalId", StringComparison.Ordinal), "receiver access is hub-scoped for the Companion App identity");
    Check(files["main.bicep"].Contains("companionAppPrincipalId: companionAppBootstrap.?outputs.identityPrincipalId ?? ''", StringComparison.Ordinal), "receiver uses the bootstrapped principal");
    foreach (var type in new[] { "namespaces", "namespaces/eventhubs", "namespaces/eventhubs/consumergroups" })
        Check(files["modules/event-hub.bicep"].Contains($"'Microsoft.EventHub/{type}@2024-01-01'", StringComparison.Ordinal), "resources include " + type);
    Check(files["modules/event-hub.bicep"].Contains("disableLocalAuth: true", StringComparison.Ordinal), "namespace disables shared-key authentication");
    Check(files["deploy.sh"].Contains(".COMPANION_EVENTHUB_NAMESPACE |= with_hyphen(50)", StringComparison.Ordinal), "MakeUniq preserves namespace length limits");
    using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(DeploymentBicepBundle.CreateArchive(files)));
    foreach (var file in new[] { "modules/event-hub.bicep", "modules/event-hub-access.bicep" }) {
        using var reader = new StreamReader(archive.GetEntry(file)!.Open());
        Check(reader.ReadToEnd() == files[file], file + " survives ZIP export");
    }
    if (args.Length > 0) {
        var directory = Path.Combine(args[0], $"enabled-{enabled}-shared-{shared}-{(consumerGroup == "$Default" ? "default" : "custom")}");
        foreach (var file in files) {
            var path = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }
    }
}
Console.WriteLine("PASS: Event Hub inputs, validation, parameters, conditional provisioning, monitor settings, RBAC ordering, and ZIP exports (8 cases).");

static void Check(bool result, string description) {
    if (!result) throw new InvalidOperationException("Failed: " + description);
}
