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
    Check(!script.Contains("export HOST1=", StringComparison.Ordinal), "Deployment Setup omits Host1 from standalone parameters");
    defaults["HEALTHPROBE_TYPE"] = "sidecar";
    foreach (var stage in new[] { "bootstrap", "application" }) {
        var json = DeploymentArmTemplate.Generate(defaults, "2.2.17", "2.0.1", new Dictionary<string, string>(), stage);
        Check(json.Contains(defaults["ACR_NAME"], StringComparison.Ordinal), stage + " uses the initialized registry name");
        Check(json.Contains(defaults["CONTAINER_APP_RESOURCE_GROUP"], StringComparison.Ordinal), stage + " uses the initialized resource group");
    }
    Check(DeploymentArmTemplate.GeneratePublishScript(defaults, "2.2.17", "2.0.1").Contains(defaults["ACR_NAME"], StringComparison.Ordinal), "publisher uses the initialized registry name");
}
Check(setupSuffixes.Count > 1, "new setup instances generate fresh suffixes");
var acaDeploymentScript = File.ReadAllText(Path.Combine(root, "deployment/ACA/deploy.sh"));
Check(!acaDeploymentScript.Contains("Host1=", StringComparison.Ordinal), "standalone ACA deployment omits Host1");
Check(!acaDeploymentScript.Contains("--environment-variables", StringComparison.Ordinal), "standalone ACA deployment does not inject container environment variables");
Console.WriteLine("PASS: randomized setup defaults, Azure name limits, unchanged references, and stable names across downloads.");

baseline["HOST1"] = "host=https://backend.example.com;path=/it's/a/test";
baseline["REQUESTAPI_LOCATION"] = baseline["LOCATION"];
var cases = 0;
string? staticBicepEntryPoint = null;
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
    var publicJson = DeploymentArmTemplate.GeneratePublicImages(values, "publicnvmacr.azurecr.io/simplel7proxy:v2.3.0", sidecar ? "publicnvmacr.azurecr.io/healthprobe:v2.0.1" : "", new Dictionary<string, string>());
    var publicTemplate = JsonNode.Parse(publicJson)!;
    var publicApp = publicTemplate["resources"]!.AsArray().Single(node => node!["name"]!.GetValue<string>().EndsWith("-container-app", StringComparison.Ordinal))!["properties"]!["template"]!["resources"]![0]!;
    Check(publicApp["properties"]!["template"]!["containers"]![0]!["image"]!.GetValue<string>() == "publicnvmacr.azurecr.io/simplel7proxy:v2.3.0", "public proxy image used directly");
    Check(publicApp["properties"]!["configuration"]!["registries"] is null, "public images need no registry credentials");
    Check(publicApp["identity"]!["type"]!.GetValue<string>() == "SystemAssigned", "public image deployment enables system-assigned identity");
    Check(publicApp["identity"]!["userAssignedIdentities"] is null, "public image deployment omits user-assigned identities");
    Check(publicApp["properties"]!["template"]!["containers"]!.AsArray().Count == (sidecar ? 2 : 1), "public image sidecar selection preserved");
    if (sidecar) Check(publicApp["properties"]!["template"]!["containers"]![1]!["image"]!.GetValue<string>() == "publicnvmacr.azurecr.io/healthprobe:v2.0.1", "public sidecar image used directly");
    Check(!publicJson.Contains("Microsoft.ContainerRegistry/registries", StringComparison.Ordinal), "public deployment omits registry resources and references");
    Check(!publicJson.Contains("7f951dda-4ed3-4680-a7ca-43fe172d538d", StringComparison.Ordinal), "public deployment omits registry pull role");
    Check(!publicJson.Contains("-registry'", StringComparison.Ordinal), "public deployment has no registry dependency");
    Check(publicJson == DeploymentArmTemplate.GeneratePublicImages(values, "publicnvmacr.azurecr.io/simplel7proxy:v2.3.0", sidecar ? "publicnvmacr.azurecr.io/healthprobe:v2.0.1" : "", new Dictionary<string, string>()), "deterministic public image export");
    var template = JsonNode.Parse(json)!;
    Check(template["$schema"]!.GetValue<string>() == "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#", "portal root uses subscription deployment scope");
    Check(!json.Contains("orchestrationGroup", StringComparison.Ordinal), "no resource-group wrapper or conditional group skip");
    var deployments = template["resources"]!.AsArray().Where(node => node!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments").ToArray();
    JsonNode Deployment(string suffix) => deployments.Single(node => node!["name"]!.GetValue<string>().EndsWith("-" + suffix, StringComparison.Ordinal))!;
    var appBootstrapDeployment = Deployment("container-app-bootstrap");
    var appBootstrap = appBootstrapDeployment["properties"]!["template"]!["resources"]![0]!;
    var appDeployment = Deployment("container-app");
    var app = appDeployment["properties"]!["template"]!["resources"]![0]!;
    Check(appBootstrap["identity"]!["type"]!.GetValue<string>() == "SystemAssigned", "Container App bootstrap enables system-assigned identity");
    Check(appBootstrap["properties"]!["configuration"]!["registries"] is null, "Container App bootstrap uses no private registry");
    Check(appBootstrap["properties"]!["template"]!["containers"]![0]!["image"]!.GetValue<string>() == DeploymentBicepBundle.ProxyImage, "Container App bootstrap uses the pinned public proxy image");
    if (sidecar) Check(appBootstrap["properties"]!["template"]!["containers"]![1]!["image"]!.GetValue<string>() == DeploymentBicepBundle.HealthProbeImage, "Container App bootstrap uses the pinned public sidecar image");
    Check(app["identity"]!["type"]!.GetValue<string>() == "SystemAssigned", "Container App enables its system-assigned managed identity");
    Check(app["identity"]!["userAssignedIdentities"] is null, "Container App omits user-assigned identities");
    Check(app["properties"]!["configuration"]!["registries"]![0]!["identity"]!.GetValue<string>() == "system", "Container App uses its system identity for ACR");
    foreach (var accessDeployment in new[] { "registry-access", "configuration-access" }.Concat(asyncMode ? ["blob-access", "service-bus-access"] : []))
        Check(appDeployment["dependsOn"]!.AsArray().Any(dependency => dependency!.GetValue<string>().Contains($"-{accessDeployment}'", StringComparison.Ordinal)), "final Container App waits for " + accessDeployment);
    Check(app["properties"]!["template"]!["containers"]!.AsArray().Count == (sidecar ? 2 : 1), "sidecar count");
    var proxyEnvironmentNames = app["properties"]!["template"]!["containers"]![0]!["env"]!.AsArray()
        .Select(setting => setting!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    Check(proxyEnvironmentNames.Contains("HealthProbeSidecar"), "Container App retains the health-probe environment setting");
    foreach (var removedSetting in new[] { "Host1", "Port", "AZURE_CLIENT_ID" })
        Check(!proxyEnvironmentNames.Contains(removedSetting), "Container App omits " + removedSetting);
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
    Check(!deployments.Any(node => node!["name"]!.GetValue<string>().EndsWith("-configuration-values", StringComparison.Ordinal)), "App Configuration values are populated after deployment");
    Check(!json.Contains("Microsoft.AppConfiguration/configurationStores/keyValues", StringComparison.Ordinal), "ARM export omits App Configuration data-plane writes");
    Check(appDeployment["dependsOn"]!.AsArray().Any(dependency => dependency!.GetValue<string>().Contains("-configuration", StringComparison.Ordinal)), "Container App waits for App Configuration store and reader role");
    var configurationResources = Deployment("configuration")["properties"]!["template"]!["resources"]!.AsArray();
    var store = configurationResources.Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.AppConfiguration/configurationStores")!;
    var dataReader = Deployment("configuration-access")["properties"]!["template"]!["resources"]!.AsArray()
        .Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Authorization/roleAssignments")!;
    Check(store["properties"]!["disableLocalAuth"]!.GetValue<bool>(), "access keys disabled");
    Check(store["properties"]!["dataPlaneProxy"]!["authenticationMode"]!.GetValue<string>() == "Pass-through", "ARM data authentication");
    Check(dataReader["scope"]!.GetValue<string>().Contains("Microsoft.AppConfiguration/configurationStores", StringComparison.Ordinal), "App Configuration Data Reader assignment is scoped to the store");
    Check(dataReader["properties"]!["roleDefinitionId"]!.GetValue<string>().Contains("516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal), "App Configuration Data Reader role is assigned");
    Check(dataReader["properties"]!["principalId"]!.GetValue<string>().Contains("Microsoft.App/containerApps", StringComparison.Ordinal), "App Configuration Data Reader targets the Container App system principal");
    Check(dataReader["properties"]!["principalId"]!.GetValue<string>().Contains("identity.principalId", StringComparison.Ordinal), "App Configuration Data Reader reads the system principal ID");
    var acrPull = Deployment("registry-access")["properties"]!["template"]!["resources"]!.AsArray()
        .Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Authorization/roleAssignments")!;
    Check(acrPull["properties"]!["roleDefinitionId"]!.GetValue<string>().Contains("7f951dda-4ed3-4680-a7ca-43fe172d538d", StringComparison.Ordinal), "ACR Pull role is assigned");
    Check(acrPull["properties"]!["principalId"]!.GetValue<string>().Contains("identity.principalId", StringComparison.Ordinal), "ACR Pull targets the Container App system principal");
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
        Check(!deployment["dependsOn"]!.AsArray().Any(dependency => dependency!.GetValue<string>().Contains($"'{values["CONTAINER_APP_NAME"]}-registry'", StringComparison.Ordinal)), "application has no bootstrap registry deployment dependency");
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
    var bicepFiles = DeploymentBicepBundle.Generate(values);
    var bicep = bicepFiles["main.bicep"];
    string[] templateAssets = [
        "bootstrap.bicep", "main.bicep", "types.bicep", "modules/registry.bicep", "modules/network.bicep",
        "modules/foundation.bicep", "modules/configuration.bicep", "modules/configuration-access.bicep",
        "modules/blob-storage.bicep", "modules/blob-access.bicep", "modules/request-api.bicep",
        "modules/service-bus-access.bicep", "modules/cosmos-access.bicep", "modules/container-app.bicep",
        "modules/registry-access.bicep", "modules/private-dns.bicep"
    ];
    Check(templateAssets.All(bicepFiles.ContainsKey), "Bicep bundle includes every static template asset");
    Check(bicepFiles.Keys.Count(file => file.EndsWith(".bicep", StringComparison.Ordinal)) == templateAssets.Length, "Bicep bundle contains only the expected template assets");
    Check(templateAssets.All(file => bicepFiles[file].EndsWith('\n')), "static Bicep assets preserve their final newline");
    Check(bicepFiles.ContainsKey("parameters.json"), "Bicep bundle includes generated deployment parameters");
    Check(bicepFiles["README.md"].EndsWith('\n') && bicepFiles["deploy.sh"].EndsWith('\n'), "bundle text assets preserve their final newline");
    Check(bicepFiles["README.md"].Contains("\n## Check after leaving the terminal\n", StringComparison.Ordinal), "bundle README recovery heading is not indented as code");
    Check(Regex.Matches(bicepFiles["README.md"], "^```bash$", RegexOptions.Multiline).Count == 5, "bundle README exposes deployment and four recovery command blocks");
    Check(bicepFiles["README.md"].Contains($"deployment='{values["CONTAINER_APP_NAME"]}-bicep'", StringComparison.Ordinal), "bundle README includes its exact subscription deployment name");
    Check(bicepFiles["deploy.sh"].Contains($"--name '{values["CONTAINER_APP_NAME"]}-bicep'", StringComparison.Ordinal), "bundle README deployment name matches its script");
    Check(bicepFiles["deploy.sh"].Contains($"--location '{values["LOCATION"]}'", StringComparison.Ordinal), "deployment script asset substitutes the selected location");
    Check(!bicepFiles["README.md"].Contains("{{DEPLOYMENT_NAME}}", StringComparison.Ordinal), "README asset deployment token is resolved");
    Check(!Regex.IsMatch(bicepFiles["deploy.sh"], "\\{\\{[A-Z_]+\\}\\}"), "deployment script asset tokens are resolved");
    Check(bicepFiles["README.md"].Contains("az deployment sub show --subscription \"$subscription\" --name \"$deployment\"", StringComparison.Ordinal), "bundle README scopes status checks to the original subscription and deployment");
    Check(bicepFiles["README.md"].Contains("az deployment sub list --subscription \"$subscription\"", StringComparison.Ordinal), "bundle README documents deployment name discovery");
    foreach (var query in new[] { "properties.provisioningState", "properties.timestamp", "properties.error", "properties.outputs.proxyUrl.value" })
        Check(bicepFiles["README.md"].Contains(query, StringComparison.Ordinal), "bundle README documents " + query);
    Check(bicep.StartsWith("targetScope = 'subscription'", StringComparison.Ordinal), "Bicep entry point has subscription scope");
    staticBicepEntryPoint ??= bicep;
    Check(bicep == staticBicepEntryPoint, "Bicep entry point is a static asset across deployment settings");
    Check(bicep.Contains("param settings DeploymentSettings", StringComparison.Ordinal), "Bicep entry point consumes the typed settings parameter");
    Check(bicep.Contains("if (settings.PRIVATE_NETWORK_DEPLOYMENT)", StringComparison.Ordinal), "Bicep entry point controls private networking from parameters");
    Check(bicep.Contains("if (settings.ASYNC_DEPLOYMENT)", StringComparison.Ordinal), "Bicep entry point controls async resources from parameters");
    Check(bicepFiles["deploy.sh"].Contains(DeploymentBicepBundle.ProxyImage, StringComparison.Ordinal), "Bicep deployment imports the pinned public proxy digest");
    Check(bicepFiles["deploy.sh"].Contains(DeploymentBicepBundle.HealthProbeImage, StringComparison.Ordinal) == sidecar, "Bicep deployment imports the pinned public sidecar digest only when selected");
    Check(!bicepFiles.ContainsKey("modules/configuration-values.bicep"), "Bicep export omits App Configuration values module");
    Check(!string.Join("\n", bicepFiles.Values).Contains("Microsoft.AppConfiguration/configurationStores/keyValues", StringComparison.Ordinal), "Bicep export omits App Configuration data-plane writes");
    Check(bicepFiles.Count(file => file.Key.StartsWith("modules/", StringComparison.Ordinal)) == 13, "Bicep bundle includes thirteen reusable resource modules");
    Check(string.Join("\n", bicepFiles.Values).Contains("Microsoft.ContainerRegistry/registries", StringComparison.Ordinal), "Bicep bundle provisions and references ACR");
    Check(bicepFiles["modules/container-app.bicep"].Contains(":v2.3.0", StringComparison.Ordinal), "Bicep Container App uses the imported proxy tag");
    Check(bicepFiles["modules/container-app.bicep"].Contains(":v2.0.1", StringComparison.Ordinal), "Bicep sidecar uses the imported HealthProbe tag");
    Check(bicepFiles["modules/container-app.bicep"].Contains("type: 'SystemAssigned'", StringComparison.Ordinal), "static Bicep enables the Container App system identity");
    Check(bicepFiles["modules/container-app.bicep"].Contains("identity: 'system'", StringComparison.Ordinal), "static Bicep uses the system identity for ACR");
    Check(!bicepFiles["modules/container-app.bicep"].Contains("userAssignedIdentities", StringComparison.Ordinal), "static Bicep omits user-assigned proxy identities");
    foreach (var removedSetting in new[] { "name: 'Host1'", "name: 'Port'", "name: 'AZURE_CLIENT_ID'" })
        Check(!bicepFiles["modules/container-app.bicep"].Contains(removedSetting, StringComparison.Ordinal), "static Bicep omits " + removedSetting);
    Check(bicepFiles["modules/configuration-access.bicep"].Contains("scope: configurationStore", StringComparison.Ordinal), "static Bicep scopes App Configuration RBAC to the store");
    Check(bicepFiles["modules/configuration-access.bicep"].Contains("516239f1-63e1-4d78-a4de-a74fb236a071", StringComparison.Ordinal), "static Bicep assigns App Configuration Data Reader");
    Check(bicepFiles["modules/configuration-access.bicep"].Contains("principalId: containerAppPrincipalId", StringComparison.Ordinal), "static Bicep assigns Data Reader to the Container App system principal");
    Check(bicepFiles["main.bicep"].Contains("containerAppBootstrap.outputs.identityPrincipalId", StringComparison.Ordinal), "Bicep access modules consume the bootstrapped Container App system principal output");
    Check(bicepFiles["main.bicep"].Contains("usePrivateRegistry: false", StringComparison.Ordinal), "Bicep creates the system identity with public images first");
    Check(bicepFiles["main.bicep"].Contains("usePrivateRegistry: true", StringComparison.Ordinal), "Bicep updates the Container App to ACR images after role assignment");
    foreach (var accessModule in new[] { "registryAccess", "configurationAccess", "blobAccess", "serviceBusAccess" })
        Check(bicepFiles["main.bicep"].Contains($"    {accessModule}\n", StringComparison.Ordinal), "final Container App update depends on " + accessModule);
    Check(!templateAssets.Select(file => bicepFiles[file]).Any(content => content.Contains(values["HOST1"], StringComparison.Ordinal)), "static Bicep assets exclude backend credentials");
    var parameterDocument = JsonNode.Parse(bicepFiles["parameters.json"])!;
    var parameterSettings = parameterDocument["parameters"]!["settings"]!["value"]!;
    var declaredSettingKeys = Regex.Matches(bicepFiles["types.bicep"], "^  ([A-Z][A-Z0-9_]+):", RegexOptions.Multiline)
        .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
    Check(declaredSettingKeys.SetEquals(parameterSettings.AsObject().Select(setting => setting.Key)), "generated parameters exactly match the static settings type");
    Check(parameterDocument["parameters"]!.AsObject().Count == 1, "parameters file contains only typed deployment settings");
    Check(parameterDocument["parameters"]!["host1"] is null, "parameters omit Host1");
    Check(parameterSettings["PRIVATE_NETWORK_DEPLOYMENT"]!.GetValue<bool>() == network, "parameters preserve the private-network toggle as Boolean");
    Check(parameterSettings["ASYNC_DEPLOYMENT"]!.GetValue<bool>() == asyncMode, "parameters preserve the async toggle as Boolean");
    Check(parameterSettings["HEALTHPROBE_TYPE"]!.GetValue<string>() == (sidecar ? "sidecar" : "internal"), "parameters preserve the health mode");
    Check(parameterSettings["WEB_PORT"]!.GetValue<int>() == 8123, "parameters preserve numeric ingress values");
    Check(parameterSettings["MAX_REPLICAS"]!.GetValue<int>() == 7, "parameters preserve numeric scaling values");
    Check(parameterSettings["RESOURCE_GROUPS"]!.AsArray().Count == (shared ? 1 : 2 + (network ? 1 : 0) + (asyncMode ? 2 : 0)), "parameters contain deduplicated selected resource groups");
    Check(parameterSettings["SERVICEBUS_RESOURCE_GROUP"]!.GetValue<string>() == values["REQUESTAPI_RESOURCE_GROUP"], "parameters default the Service Bus resource group explicitly");
    Check(parameterSettings["COSMOS_RESOURCE_GROUP"]!.GetValue<string>() == values["REQUESTAPI_RESOURCE_GROUP"], "parameters default the Cosmos resource group explicitly");
    var archiveBytes = DeploymentBicepBundle.CreateArchive(bicepFiles);
    Check(archiveBytes.SequenceEqual(DeploymentBicepBundle.CreateArchive(DeploymentBicepBundle.Generate(values))), "deterministic Bicep ZIP");
    using (var archive = new System.IO.Compression.ZipArchive(new MemoryStream(archiveBytes))) {
        Check(archive.Entries.Count == bicepFiles.Count, "Bicep ZIP includes every file");
        foreach (var entry in archive.Entries) {
            using var reader = new StreamReader(entry.Open());
            Check(reader.ReadToEnd() == bicepFiles[entry.FullName], "Bicep ZIP content roundtrip");
        }
    }
    if (args.Length > 0) {
        var directory = args[0] + ".bicep/case-" + cases;
        foreach (var file in bicepFiles) {
            var path = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }
        using var syntax = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash") {
            ArgumentList = { "-n", Path.Combine(directory, "deploy.sh") }, UseShellExecute = false
        })!;
        syntax.WaitForExit();
        Check(syntax.ExitCode == 0, "Bicep deployment script Bash syntax");
    }
    cases++;
}
Console.WriteLine($"PASS: {cases} ARM and Bicep export scenarios; staged isolation, public images, deterministic ZIPs, scripts, resource toggles, groups, and local modules.");

if (args.Length > 0) {
    var scriptDirectory = Path.GetFullPath(args[0] + ".bicep/case-0");
    foreach (var scenario in new (string[] Arguments, string? Operation, int ExitCode, int AzureExitCode)[] {
        ([], null, 1, 0),
        ([""], null, 1, 0),
        (["--unexpected"], null, 1, 0),
        (["test-subscription", "delete"], null, 1, 0),
        (["test-subscription", "create", "extra"], null, 1, 0),
        (["--help"], null, 0, 0),
        (["-h"], null, 0, 0),
        (["test-subscription"], "validate", 0, 0),
        (["test-subscription", ""], "validate", 0, 0),
        (["test-subscription", "validate"], "validate", 0, 0),
        (["test-subscription", "what-if"], "what-if", 0, 0),
        (["test-subscription", "create"], "create", 0, 0),
        (["test-subscription", "create"], "create", 17, 17)
    }) {
        var startInfo = new System.Diagnostics.ProcessStartInfo("bash") {
            ArgumentList = { "--noprofile", "--norc", "-s", "--", Path.Combine(scriptDirectory, "deploy.sh") },
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in scenario.Arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Remove("BASH_ENV");
        startInfo.Environment["MOCK_AZ_EXIT_CODE"] = scenario.AzureExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var execution = System.Diagnostics.Process.Start(startInfo)!;
        execution.StandardInput.WriteLine("az() { printf '%s\\n' \"$@\"; return \"$MOCK_AZ_EXIT_CODE\"; }");
        execution.StandardInput.WriteLine("export -f az");
        execution.StandardInput.WriteLine("bash \"$@\"");
        execution.StandardInput.Close();
        var standardOutput = execution.StandardOutput.ReadToEnd();
        var standardError = execution.StandardError.ReadToEnd();
        execution.WaitForExit();
        Check(execution.ExitCode == scenario.ExitCode, "deployment script exit code: " + string.Join(" ", scenario.Arguments));
        if (scenario.Operation is not null) {
            var expectedOutput = new List<string>();
            if (scenario.Operation == "create") {
                expectedOutput.AddRange([
                    "deployment", "sub", "create", "--subscription", "test-subscription",
                    "--name", baseline["CONTAINER_APP_NAME"] + "-bicep-bootstrap", "--location", baseline["LOCATION"],
                    "--template-file", Path.Combine(scriptDirectory, "bootstrap.bicep"),
                    "--parameters", "@" + Path.Combine(scriptDirectory, "parameters.json")
                ]);
                if (scenario.AzureExitCode == 0) {
                    expectedOutput.AddRange([
                        "acr", "import", "--subscription", "test-subscription", "--resource-group", baseline["CONTAINER_APP_RESOURCE_GROUP"], "--name", baseline["ACR_NAME"],
                        "--source", DeploymentBicepBundle.ProxyImage, "--image", baseline["PROXY_IMAGE_NAME"] + ":v2.3.0", "--force"
                    ]);
                }
            }
            if (scenario.Operation != "create" || scenario.AzureExitCode == 0) expectedOutput.AddRange([
                "deployment", "sub", scenario.Operation, "--subscription", "test-subscription",
                "--name", baseline["CONTAINER_APP_NAME"] + "-bicep", "--location", baseline["LOCATION"],
                "--template-file", Path.Combine(scriptDirectory, "main.bicep"),
                "--parameters", "@" + Path.Combine(scriptDirectory, "parameters.json")
            ]);
            Check(standardOutput.TrimEnd('\n').Split('\n').SequenceEqual(expectedOutput), "deployment script forwards exact Azure arguments from another working directory");
            Check(standardError.Length == 0, "deployment script preserves Azure output without extra diagnostics");
        } else if (scenario.ExitCode == 0) {
            Check(standardOutput.StartsWith("Usage:", StringComparison.Ordinal) && standardError.Length == 0, "deployment script help succeeds without invoking Azure");
        } else {
            Check(standardOutput.Length == 0 && standardError.Contains("Error:", StringComparison.Ordinal), "deployment script rejects invalid arguments before invoking Azure");
        }
    }
    Console.WriteLine("PASS: 13 mocked deployment-script cases; help, input errors, default operation, exact arguments, working-directory independence, and Azure exit codes.");
}

baseline["PRIVATE_NETWORK_DEPLOYMENT"] = "no";
baseline["ASYNC_DEPLOYMENT"] = "no";
baseline["HEALTHPROBE_TYPE"] = "internal";
baseline["ENABLE_APP_INSIGHTS"] = "false";
baseline["UPDATE_CONTAINER_APP_ENV"] = "false";
baseline["APPCONFIG_LABEL"] = "test~$label";
var escaped = DeploymentArmTemplate.Generate(baseline, "2.2.17", "2.0.1", new Dictionary<string, string> { ["Warm:Custom"] = "[literal]" });
Check(!escaped.Contains("test~7E~24label", StringComparison.Ordinal), "configuration label is not embedded in a key-value resource name");
Check(!escaped.Contains("[[literal]", StringComparison.Ordinal), "configuration defaults are not embedded in infrastructure");
Check(!escaped.Contains("Microsoft.Insights/components", StringComparison.Ordinal), "disabled insights omitted");
Check(!escaped.Contains("AZURE_APPCONFIG_ENDPOINT", StringComparison.Ordinal), "disabled App Configuration wiring omitted");
Console.WriteLine("PASS: configuration data omitted and optional configuration disabled.");

baseline["HEALTHPROBE_TYPE"] = "sidecar";
var missingHealthImageRejected = false;
try {
    DeploymentArmTemplate.GeneratePublicImages(baseline, "publicnvmacr.azurecr.io/simplel7proxy:v2.3.0", "", new Dictionary<string, string>());
} catch (InvalidOperationException exception) when (exception.Message.Contains("public HealthProbe image", StringComparison.Ordinal)) {
    missingHealthImageRejected = true;
}
Check(missingHealthImageRejected, "public export rejects missing sidecar image without changing health mode");
Console.WriteLine("PASS: public images, retained managed identity, omitted registry access, and missing sidecar validation.");

var mixedCaseGroups = new Dictionary<string, string>(baseline) {
    ["CONTAINER_APP_RESOURCE_GROUP"] = "rg-shared-case",
    ["APPCONFIG_RESOURCE_GROUP"] = "RG-SHARED-CASE"
};
var mixedCaseBundle = DeploymentBicepBundle.Generate(mixedCaseGroups);
var mixedCaseSettings = JsonNode.Parse(mixedCaseBundle["parameters.json"])!["parameters"]!["settings"]!["value"]!;
Check(mixedCaseSettings["RESOURCE_GROUPS"]!.AsArray().Count == 1, "parameters deduplicate resource-group names case-insensitively");
Check(mixedCaseSettings["RESOURCE_GROUPS"]![0]!.GetValue<string>() == "rg-shared-case", "parameters preserve the first resource-group spelling");
if (args.Length > 0) {
    foreach (var file in mixedCaseBundle) {
        var path = Path.Combine(args[0] + ".bicep/case-insensitive", file.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, file.Value);
    }
}
Console.WriteLine("PASS: Bicep parameters preserve Azure resource-group case insensitivity.");

static void Check(bool result, string description) {
    if (!result) throw new InvalidOperationException("Failed: " + description);
}