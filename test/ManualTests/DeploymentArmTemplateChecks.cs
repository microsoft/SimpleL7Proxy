#:project ../../src/CompanionApp/CompanionApp.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanionApp.Components.Pages;

var root = Directory.GetCurrentDirectory();
var baseline = File.ReadLines(Path.Combine(root, "deployment/deploy.parameters.example.sh"))
    .Where(line => line.StartsWith("export ", StringComparison.Ordinal))
    .Select(line => Regex.Match(line, "^export ([A-Z][A-Z0-9_]*)=(?:\"([^\"]*)\"|([^ #]+))"))
    .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
var setupFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
var initializeSetup = typeof(DeploymentSetupPage).GetMethod("OnInitialized", setupFlags)!;
var readSetupValue = typeof(DeploymentSetupPage).GetMethod("Value", setupFlags)!;
var exportSetupScript = typeof(DeploymentSetupPage).GetMethod("GenerateScript", setupFlags)!;
var selectSetupTab = typeof(DeploymentSetupPage).GetMethod("SelectTab", setupFlags)!;
string[] compactResourceKeys = ["ACR_NAME", "STORAGE_ACCOUNT_NAME", "REQUESTAPI_STORAGE_ACCOUNT"];
string[] hyphenatedResourceKeys = [
    "NETWORK_RESOURCE_GROUP", "CONTAINER_APP_RESOURCE_GROUP", "STORAGE_RESOURCE_GROUP", "APPCONFIG_RESOURCE_GROUP", "REQUESTAPI_RESOURCE_GROUP",
    "ACA_ENVIRONMENT_NAME", "CONTAINER_APP_NAME", "ENVIRONMENT_NAME", "LOG_ANALYTICS_WORKSPACE_NAME", "APPCONFIG_NAME", "VNET_NAME",
    "REQUESTAPI_FUNCTION_APP", "REQUESTAPI_APPINSIGHTS_NAME", "ACA_RECORD_NAME"
];
var resourceNameKeys = compactResourceKeys.Concat(hyphenatedResourceKeys).ToHashSet(StringComparer.Ordinal);
var setupSuffixes = new HashSet<string>(StringComparer.Ordinal);
for (var setupIndex = 0; setupIndex < 4; setupIndex++) {
    var setup = new DeploymentSetupPage();
    initializeSetup.Invoke(setup, null);
    var defaults = baseline.Keys.ToDictionary(key => key, key => (string)readSetupValue.Invoke(setup, [key])!);
    var suffix = defaults["ACR_NAME"][baseline["ACR_NAME"].Length..];
    Check(Regex.IsMatch(suffix, "^[a-z0-9]{5}$"), "five-character lowercase alphanumeric default suffix");
    setupSuffixes.Add(suffix);
    foreach (var key in compactResourceKeys) Check(defaults[key] == baseline[key] + suffix, key + " shares suffix without a separator");
    foreach (var key in hyphenatedResourceKeys) Check(defaults[key] == baseline[key] + "-" + suffix, key + " shares suffix with a separator");
    foreach (var key in baseline.Keys.Where(key => !resourceNameKeys.Contains(key))) {
        var expected = key is "PRIVATE_NETWORK_DEPLOYMENT" or "ASYNC_DEPLOYMENT" ? "no" : key == "REQUESTAPI_LOCATION" ? "" : baseline[key];
        Check(defaults[key] == expected, key + " remains unchanged by resource naming");
    }
    Check(Regex.IsMatch(defaults["ACR_NAME"], "^[a-z0-9]{5,50}$"), "default registry name satisfies Azure limits");
    foreach (var key in new[] { "STORAGE_ACCOUNT_NAME", "REQUESTAPI_STORAGE_ACCOUNT" })
        Check(Regex.IsMatch(defaults[key], "^[a-z0-9]{3,24}$"), key + " satisfies Azure storage limits");
    Check(Regex.IsMatch(defaults["CONTAINER_APP_NAME"], "^[a-z][a-z0-9-]{0,30}[a-z0-9]$") && !defaults["CONTAINER_APP_NAME"].Contains("--"), "default Container App name satisfies Azure limits");
    Check(defaults["ACA_RECORD_NAME"] == defaults["CONTAINER_APP_NAME"], "default DNS record stays aligned with Container App");
    var script = (string)exportSetupScript.Invoke(setup, null)!;
    foreach (var tab in new[] { "images", "app", "config", "review" }) selectSetupTab.Invoke(setup, [tab, false]);
    Check(script == (string)exportSetupScript.Invoke(setup, null)!, "tab changes and repeat downloads preserve generated names");
    Check(script.Contains($"export ACA_ENVIRONMENT_NAME='{defaults["ENVIRONMENT_NAME"]}'", StringComparison.Ordinal), "standalone environment alias uses the suffixed name");
    defaults["HEALTHPROBE_TYPE"] = "sidecar";
    foreach (var stage in new[] { "bootstrap", "application" }) {
        var json = DeploymentArmTemplate.Generate(defaults, "2.2.17", "2.0.1", new Dictionary<string, string>(), stage);
        Check(json.Contains(defaults["ACR_NAME"], StringComparison.Ordinal), stage + " uses the initialized registry name");
        Check(json.Contains(defaults["CONTAINER_APP_RESOURCE_GROUP"], StringComparison.Ordinal), stage + " uses the initialized resource group");
    }
    Check(DeploymentArmTemplate.GeneratePublishScript(defaults, "2.2.17", "2.0.1").Contains(defaults["ACR_NAME"], StringComparison.Ordinal), "publisher uses the initialized registry name");
}
Check(setupSuffixes.Count > 1, "new setup instances generate fresh suffixes");
Console.WriteLine("PASS: randomized setup defaults, Azure name limits, unchanged references, and stable names across downloads.");

baseline["HOST1"] = "host=https://backend.example.com;path=/it's/a/test";
baseline["REQUESTAPI_LOCATION"] = baseline["LOCATION"];
var cases = 0;
foreach (var shared in new[] { false, true })
foreach (var network in new[] { false, true })
foreach (var asyncMode in new[] { false, true })
foreach (var sidecar in new[] { false, true }) {
    var values = new Dictionary<string, string>(baseline);
    values["PRIVATE_NETWORK_DEPLOYMENT"] = network ? "yes" : "no";
    values["ASYNC_DEPLOYMENT"] = asyncMode ? "yes" : "no";
    values["HEALTHPROBE_TYPE"] = sidecar ? "sidecar" : "internal";
    values["WEB_PORT"] = "8123";
    values["MAX_REPLICAS"] = "7";
    if (shared) foreach (var key in values.Keys.Where(key => key.EndsWith("RESOURCE_GROUP", StringComparison.Ordinal)).ToArray()) values[key] = "rg-shared";
    var json = DeploymentArmTemplate.Generate(values, "2.2.17", "2.0.1", new Dictionary<string, string> { ["Warm:Sentinel"] = "", ["Cold:Server:Port"] = "8000" });
    var template = JsonNode.Parse(json)!;
    Check(template["$schema"]!.GetValue<string>() == "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#", "portal root uses subscription deployment scope");
    Check(!json.Contains("orchestrationGroup", StringComparison.Ordinal), "no resource-group wrapper or conditional group skip");
    var deployments = template["resources"]!.AsArray().Where(node => node!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments").ToArray();
    JsonNode Deployment(string suffix) => deployments.Single(node => node!["name"]!.GetValue<string>().EndsWith("-" + suffix, StringComparison.Ordinal))!;
    var app = Deployment("container-app")["properties"]!["template"]!["resources"]![0]!;
    Check(app["properties"]!["template"]!["containers"]!.AsArray().Count == (sidecar ? 2 : 1), "sidecar count");
    Check(app["properties"]!["configuration"]!["ingress"]!["targetPort"]!.GetValue<int>() == 8123, "target port");
    Check(app["properties"]!["template"]!["scale"]!["maxReplicas"]!.GetValue<int>() == 7, "replicas");
    Check(app["properties"]!["template"]!["containers"]![0]!["image"]!.GetValue<string>().Contains("'v2.2.17'", StringComparison.Ordinal), "proxy tag matches publisher");
    if (sidecar) Check(app["properties"]!["template"]!["containers"]![1]!["image"]!.GetValue<string>().Contains("'v2.0.1'", StringComparison.Ordinal), "sidecar tag matches publisher");
    Check(!json.Contains("templateLink", StringComparison.Ordinal), "no external templates");
    Check(json == DeploymentArmTemplate.Generate(values, "2.2.17", "2.0.1", new Dictionary<string, string> { ["Warm:Sentinel"] = "", ["Cold:Server:Port"] = "8000" }), "deterministic export");
    Check(deployments.Any(node => node!["name"]!.GetValue<string>().EndsWith("-network", StringComparison.Ordinal)) == network, "network toggle");
    Check(deployments.Any(node => node!["name"]!.GetValue<string>().EndsWith("-request-api", StringComparison.Ordinal)) == asyncMode, "async toggle");
    var groups = template["resources"]!.AsArray().Where(node => node!["type"]!.GetValue<string>() == "Microsoft.Resources/resourceGroups").ToArray();
    Check(groups.Length == (shared ? 1 : 2 + (network ? 1 : 0) + (asyncMode ? 2 : 0)), "group deduplication");
    Check(groups.All(group => group!["condition"] is null), "group creation is unconditional");
    if (asyncMode) foreach (var parameter in new[] { "serviceBusResourceGroup", "cosmosResourceGroup" }) {
        Check(template["parameters"]![parameter]!["type"]!.GetValue<string>() == "string", "external group parameter declared at portal root");
        Check(deployments.All(deployment => deployment!["properties"]!["parameters"]![parameter]!["value"]!.GetValue<string>() == $"[parameters('{parameter}')]"), "external group parameter forwarded to child deployments");
    }
    var keys = Deployment("configuration-values")["properties"]!["template"]!["resources"]!.AsArray();
    Check(keys.Single(node => node!["name"]!.GetValue<string>().Contains("Warm~3AHost1$", StringComparison.Ordinal))!["properties"]!["value"]!.GetValue<string>() == values["HOST1"], "Host1 roundtrip");
    var store = Deployment("configuration")["properties"]!["template"]!["resources"]![0]!;
    Check(store["properties"]!["disableLocalAuth"]!.GetValue<bool>(), "access keys disabled");
    Check(store["properties"]!["dataPlaneProxy"]!["authenticationMode"]!.GetValue<string>() == "Pass-through", "ARM data authentication");
    if (network) {
        var dns = Deployment("private-dns")["properties"]!;
        Check(dns["parameters"]!["environmentDomain"]!["value"]!.GetValue<string>().Contains("reference(", StringComparison.Ordinal), "DNS domain passed across template boundary");
        Check(dns["template"]!["resources"]!.AsArray().All(node => !node!["name"]!.GetValue<string>().Contains("reference(", StringComparison.Ordinal)), "no runtime references in resource names");
    }
    foreach (var deployment in deployments) {
        Check(deployment!["properties"]!["template"]!["$schema"]!.GetValue<string>() == "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#", "service templates retain resource-group scope");
        Check(deployment!["properties"]!["expressionEvaluationOptions"]!["scope"]!.GetValue<string>() == "inner", "inner evaluation scope");
        Check(deployment["dependsOn"]!.AsArray().All(node => node!.GetValue<string>().StartsWith('[')), "scoped dependency IDs");
        var targetGroup = deployment["resourceGroup"]!.GetValue<string>();
        if (!targetGroup.StartsWith("[parameters(", StringComparison.Ordinal)) {
            Check(groups.Any(group => group!["name"]!.GetValue<string>() == targetGroup), "target group declared alongside deployment");
            Check(deployment["dependsOn"]!.AsArray().Any(node => node!.GetValue<string>() == $"[subscriptionResourceId('Microsoft.Resources/resourceGroups', '{targetGroup}')]"), "direct dependency on target group");
        }
        Check(!deployment["dependsOn"]!.ToJsonString().Contains("-resource-groups", StringComparison.Ordinal), "no dependency on containing deployment");
    }
    if (args.Length > 0 && shared && network && asyncMode && sidecar) File.WriteAllText(args[0], json);
    var bootstrap = JsonNode.Parse(DeploymentArmTemplate.Generate(values, "2.2.17", "2.0.1", new Dictionary<string, string>(), "bootstrap"))!;
    var bootstrapDeployments = bootstrap["resources"]!.AsArray().Where(node => node!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments").ToArray();
    Check(bootstrapDeployments.Length == 1, "bootstrap has only registry deployment");
    Check(bootstrapDeployments[0]!["properties"]!["template"]!["resources"]!.AsArray().Single()!["type"]!.GetValue<string>() == "Microsoft.ContainerRegistry/registries", "bootstrap creates only ACR within groups");
    Check(bootstrap["parameters"] is null, "bootstrap has no external async parameters");
    var application = JsonNode.Parse(DeploymentArmTemplate.Generate(values, "2.2.17", "2.0.1", new Dictionary<string, string>(), "application"))!;
    Check(application["resources"]!.AsArray().All(node => node!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments"), "application does not recreate groups");
    foreach (var deployment in application["resources"]!.AsArray()) {
        Check(!deployment!["dependsOn"]!.ToJsonString().Contains("Microsoft.Resources/resourceGroups", StringComparison.Ordinal), "application has no bootstrap group dependency");
        Check(!deployment["dependsOn"]!.ToJsonString().Contains("-registry", StringComparison.Ordinal), "application has no bootstrap registry deployment dependency");
        Check(deployment["properties"]!["template"]!["resources"]!.AsArray().All(node => node!["type"]!.GetValue<string>() != "Microsoft.ContainerRegistry/registries"), "application reuses ACR");
    }
    foreach (var method in new[] { "remote", "local" }) {
        values["BUILD_METHOD"] = method;
        var script = DeploymentArmTemplate.GeneratePublishScript(values, "2.2.17", "2.0.1");
        Check(script.Contains(values["PROXY_IMAGE_NAME"] + ":v2.2.17", StringComparison.Ordinal), "publish proxy tag matches ARM");
        Check(script.Contains(values["HEALTH_IMAGE_NAME"] + ":v2.0.1", StringComparison.Ordinal) == sidecar, "publish sidecar toggle and tag");
        Check(script.Contains("az acr build", StringComparison.Ordinal) == (method == "remote"), "selected build method");
        Check(!script.Contains(values["HOST1"], StringComparison.Ordinal), "publishing script excludes backend credentials");
        Check(script.Contains("${1:?Usage:", StringComparison.Ordinal), "publishing requires explicit subscription");
        if (args.Length > 0) {
            var scriptPath = args[0] + "." + method + "." + sidecar + ".sh";
            File.WriteAllText(scriptPath, script);
            using var syntax = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash") {
                ArgumentList = { "-n", scriptPath }, UseShellExecute = false
            })!;
            syntax.WaitForExit();
            Check(syntax.ExitCode == 0, "generated publishing script Bash syntax");
        }
    }
    cases++;
}
Console.WriteLine($"PASS: {cases} ARM export scenarios; bootstrap/application isolation, image tags, local/remote publishing scripts, resource toggles, groups, and embedded templates.");

baseline["PRIVATE_NETWORK_DEPLOYMENT"] = "no";
baseline["ASYNC_DEPLOYMENT"] = "no";
baseline["HEALTHPROBE_TYPE"] = "internal";
baseline["ENABLE_APP_INSIGHTS"] = "false";
baseline["UPDATE_CONTAINER_APP_ENV"] = "false";
baseline["APPCONFIG_LABEL"] = "test~$label";
var escaped = DeploymentArmTemplate.Generate(baseline, "2.2.17", "2.0.1", new Dictionary<string, string> { ["Warm:Custom"] = "[literal]" });
Check(escaped.Contains("test~7E~24label", StringComparison.Ordinal), "label resource-name encoding");
Check(escaped.Contains("[[literal]", StringComparison.Ordinal), "ARM expression escaping");
Check(!escaped.Contains("Microsoft.Insights/components", StringComparison.Ordinal), "disabled insights omitted");
Check(!escaped.Contains("AZURE_APPCONFIG_ENDPOINT", StringComparison.Ordinal), "disabled App Configuration wiring omitted");
Console.WriteLine("PASS: literal escaping, encoded labels, and disabled optional configuration.");

static void Check(bool result, string description) {
    if (!result) throw new InvalidOperationException("Failed: " + description);
}