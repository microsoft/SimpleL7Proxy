#:project ../../src/CompanionApp/CompanionApp.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CompanionApp.Components.Pages;

var root = Directory.GetCurrentDirectory();
var baseline = File.ReadLines(Path.Combine(root, "deployment/interactive/deploy.parameters.example.sh"))
    .Where(line => line.StartsWith("export ", StringComparison.Ordinal))
    .Select(line => Regex.Match(line, "^export ([A-Z][A-Z0-9_]*)=(?:\"([^\"]*)\"|([^ #]+))"))
    .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
baseline["PRIVATE_NETWORK_DEPLOYMENT"] = "no";
baseline["ASYNC_DEPLOYMENT"] = "no";
baseline["HEALTHPROBE_TYPE"] = "internal";
baseline["REQUESTAPI_LOCATION"] = baseline["LOCATION"];

var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var setup = new DeploymentSetupPage();
typeof(DeploymentSetupPage).GetMethod("OnInitialized", flags)!.Invoke(setup, null);
var readValue = typeof(DeploymentSetupPage).GetMethod("Value", flags)!;
Check((string)readValue.Invoke(setup, ["DEPLOY_STORAGE_NETWORK"])! == "true", "Simple mode enables storage networking");
Check((string)readValue.Invoke(setup, ["PRIVATE_NETWORK_DEPLOYMENT"])! == "no", "Simple mode keeps public application ingress");
Check((string)readValue.Invoke(setup, ["ASYNC_DEPLOYMENT"])! == "no", "Simple mode does not enable async services");
var resetMode = typeof(DeploymentSetupPage).GetMethod("ResetMode", flags)!;
resetMode.Invoke(setup, [false]);
Check((string)readValue.Invoke(setup, ["DEPLOY_STORAGE_NETWORK"])! == "false", "Advanced defaults remain unchanged");
resetMode.Invoke(setup, [true]);
Check((string)readValue.Invoke(setup, ["DEPLOY_STORAGE_NETWORK"])! == "true", "Simple mode restores storage networking");

var cases = 0;
foreach (var privateIngress in new[] { false, true })
foreach (var asyncMode in new[] { false, true })
foreach (var storageNetwork in new[] { false, true })
foreach (var sharedGroup in new[] { false, true }) {
    var values = new Dictionary<string, string>(baseline) {
        ["PRIVATE_NETWORK_DEPLOYMENT"] = privateIngress ? "yes" : "no",
        ["ASYNC_DEPLOYMENT"] = asyncMode ? "yes" : "no",
        ["DEPLOY_STORAGE_NETWORK"] = storageNetwork ? "true" : "false",
        ["NETWORK_RESOURCE_GROUP"] = "rg-network",
        ["STORAGE_RESOURCE_GROUP"] = "rg-storage",
        ["REQUESTAPI_RESOURCE_GROUP"] = "rg-request-api"
    };
    if (sharedGroup) foreach (var key in values.Keys.Where(key => key.EndsWith("RESOURCE_GROUP", StringComparison.Ordinal)).ToArray()) values[key] = "rg-shared";
    var template = JsonNode.Parse(DeploymentArmTemplate.Generate(values, "2.3.0", "2.0.1", new Dictionary<string, string>()))!;
    var deployments = template["resources"]!.AsArray().Where(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments").ToArray();
    JsonNode Deployment(string suffix) => deployments.Single(resource => resource!["name"]!.GetValue<string>().EndsWith("-" + suffix, StringComparison.Ordinal))!;
    Check(deployments.Any(resource => resource!["name"]!.GetValue<string>().EndsWith("-network", StringComparison.Ordinal)) == (privateIngress || storageNetwork), "network enablement");
    Check(deployments.Any(resource => resource!["name"]!.GetValue<string>().EndsWith("-blob-storage", StringComparison.Ordinal)) == (asyncMode || storageNetwork), "storage enablement independent of async");
    Check(deployments.Any(resource => resource!["name"]!.GetValue<string>().EndsWith("-request-api", StringComparison.Ordinal)) == asyncMode, "RequestAPI enablement unchanged");
    Check(deployments.Any(resource => resource!["name"]!.GetValue<string>().EndsWith("-storage-private-endpoint", StringComparison.Ordinal)) == storageNetwork, "endpoint enablement");
    if (privateIngress || storageNetwork) {
        var environment = Deployment("foundation")["properties"]!["template"]!["resources"]!.AsArray()
            .Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.App/managedEnvironments")!;
        Check(environment["properties"]!["vnetConfiguration"]!["internal"]!.GetValue<bool>() == privateIngress, "storage networking does not privatize app ingress");
        var subnets = Deployment("network")["properties"]!["template"]!["resources"]![0]!["properties"]!["subnets"]!.AsArray();
        Check(subnets.Count == (privateIngress ? 5 : asyncMode ? 3 : 2), "only required subnets are created");
    }
    if (asyncMode || storageNetwork) {
        var storage = Deployment("blob-storage")["properties"]!["template"]!["resources"]![0]!;
        Check(storage["properties"]!["publicNetworkAccess"]!.GetValue<string>() == (storageNetwork ? "Disabled" : "Enabled"), "storage public access follows storage networking");
        Check(!storage["properties"]!["allowSharedKeyAccess"]!.GetValue<bool>() && !storage["properties"]!["allowBlobPublicAccess"]!.GetValue<bool>(), "storage keeps key and anonymous access disabled");
    }
    if (storageNetwork) {
        var endpointResources = Deployment("storage-private-endpoint")["properties"]!["template"]!["resources"]!.AsArray();
        var endpoint = endpointResources.Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Network/privateEndpoints")!;
        Check(endpoint["properties"]!["subnet"]!["id"]!.GetValue<string>().Contains(values["SUBNET_PRIVATEENDPOINTS_NAME"], StringComparison.Ordinal), "dedicated storage endpoint subnet");
        Check(endpoint["properties"]!["privateLinkServiceConnections"]![0]!["properties"]!["groupIds"]![0]!.GetValue<string>() == "blob", "Blob private-link target");
        Check(endpointResources.Any(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Network/privateDnsZones/virtualNetworkLinks"), "DNS linked to the VNet");
        Check(endpointResources.Any(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Network/privateEndpoints/privateDnsZoneGroups"), "DNS zone group registers the endpoint");
        Check(Deployment("storage-private-endpoint")["dependsOn"]!.ToJsonString().Contains("-blob-storage", StringComparison.Ordinal), "endpoint waits for storage");
        Check(Deployment("blob-access").ToJsonString().Contains("ba92f5b4-2d11-453d-a403-e96b0029c9fe", StringComparison.Ordinal), "proxy receives Blob data access");
        Check(!asyncMode || Deployment("request-api").ToJsonString().Contains("virtualNetworkSubnetId", StringComparison.Ordinal), "async clients join the VNet");
    }
    var files = DeploymentBicepBundle.Generate(values);
    var settings = JsonNode.Parse(files["parameters.json"])!["parameters"]!["settings"]!["value"]!;
    Check(settings["DEPLOY_STORAGE_NETWORK"]!.GetValue<bool>() == storageNetwork, "Bicep flag matches ARM");
    if (storageNetwork) {
        var groups = settings["RESOURCE_GROUPS"]!.AsArray().Select(group => group!.GetValue<string>()).ToArray();
        Check(groups.Contains(values["NETWORK_RESOURCE_GROUP"]) && groups.Contains(values["STORAGE_RESOURCE_GROUP"]), "Bicep bootstrap includes network and storage groups");
        Check(!groups.Contains(values["REQUESTAPI_RESOURCE_GROUP"]) || asyncMode || sharedGroup, "storage alone needs no RequestAPI group");
    }
    if (args.Length > 0 && storageNetwork && !privateIngress && !asyncMode && !sharedGroup) {
        foreach (var file in files) {
            var path = Path.Combine(args[0], file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Value);
        }
    }
    cases++;
}
Console.WriteLine($"PASS: Simple/Advanced defaults and {cases} public/private ingress, async, storage-network and resource-group combinations.");

var legacyValues = new Dictionary<string, string>(baseline);
legacyValues.Remove("DEPLOY_STORAGE_NETWORK");
var legacyBundle = DeploymentBicepBundle.Generate(legacyValues);
Check(!JsonNode.Parse(legacyBundle["parameters.json"])!["parameters"]!["settings"]!["value"]!["DEPLOY_STORAGE_NETWORK"]!.GetValue<bool>(), "older callers default storage networking to false");
Check(!DeploymentArmTemplate.Generate(legacyValues, "2.3.0", "2.0.1", new Dictionary<string, string>()).Contains("Microsoft.Network/privateEndpoints", StringComparison.Ordinal), "older ARM callers do not create private endpoints");
var existingEnvironment = new Dictionary<string, string>(baseline) {
    ["DEPLOY_STORAGE_NETWORK"] = "true", ["USE_EXISTING_ENVIRONMENT"] = "true"
};
foreach (var export in new Action[] {
    () => DeploymentBicepBundle.Generate(existingEnvironment),
    () => DeploymentArmTemplate.Generate(existingEnvironment, "2.3.0", "2.0.1", new Dictionary<string, string>())
}) {
    var rejected = false;
    try {
        export();
    } catch (InvalidOperationException exception) when (exception.Message.Contains("requires a new Container Apps environment", StringComparison.Ordinal)) {
        rejected = true;
    }
    Check(rejected, "existing environments cannot silently lose access to private storage");
}
Console.WriteLine("PASS: backward-compatible missing flag and existing-environment validation in both exporters.");

static void Check(bool result, string description) {
    if (!result) throw new InvalidOperationException("Failed: " + description);
}
