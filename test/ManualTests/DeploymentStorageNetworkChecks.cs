#:project ../../src/CompanionApp/CompanionApp.csproj
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Reflection;
using System.Diagnostics;
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
        var profiles = Deployment("profiles");
        var profileResources = profiles["properties"]!["template"]!["resources"]!.AsArray();
        var job = profileResources.Single(resource => resource!["type"]!.GetValue<string>() == "Microsoft.App/jobs")!;
        Check(job["properties"]!["configuration"]!["triggerType"]!.GetValue<string>() == "Schedule", "Portal seeding executes automatically, not an unstarted manual job");
        Check(job["properties"]!["configuration"]!["scheduleTriggerConfig"]!["cronExpression"]!.GetValue<string>() == "*/5 * * * *", "Portal seeding is scheduled every five minutes");
        Check(job["identity"]!["type"]!.GetValue<string>() == "SystemAssigned", "seed uses managed identity");
        Check(profiles["properties"]!["parameters"]!["environmentId"]!["value"]!.GetValue<string>().Contains("Microsoft.App/managedEnvironments", StringComparison.Ordinal), "seed runs in the VNet-attached application environment");
        foreach (var role in profileResources.Where(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Authorization/roleAssignments"))
            Check(role!["scope"]!.GetValue<string>().Contains("blobServices/containers", StringComparison.Ordinal)
                && role["scope"]!.GetValue<string>().Contains("profiles", StringComparison.Ordinal), "all profile roles are container scoped");
        Check(profiles["properties"]!["template"]!["variables"]!["contributorRoleId"]!.GetValue<string>() == "ba92f5b4-2d11-453d-a403-e96b0029c9fe", "profiles use the mandatory Contributor role");
        Check(profiles["dependsOn"]!.ToJsonString().Contains("-storage-private-endpoint", StringComparison.Ordinal), "seed waits for private endpoint/DNS deployment");
        Check(profiles["properties"]!["parameters"]!["appPrincipalIds"]!["value"]!.AsArray().Count == 1, "Portal grants only its deployed proxy identity, not an undeployed Companion app");
        Check(deployments.Any(resource => resource!["name"]!.GetValue<string>().EndsWith("-blob-access", StringComparison.Ordinal)) == asyncMode, "account-scoped async Blob role remains async-only");
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

var sample = File.ReadAllText(Path.Combine(root, "deployment/assets/config.json")).Replace("\r\n", "\n", StringComparison.Ordinal);
var seedSource = File.ReadAllText(Path.Combine(root, "deployment/assets/seed-profiles.py")).Replace("\r\n", "\n", StringComparison.Ordinal);
var compiledProfiles = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "deployment/modules/profiles.json")))!;
var compiledStrings = compiledProfiles["variables"]!.AsObject().Select(pair => pair.Value!.GetValue<string>()).ToArray();
Check(compiledStrings.Contains(sample) && compiledStrings.Contains(seedSource), "compiled ARM module is regenerated from canonical sample and script");
foreach (var deployProxy in new[] { false, true })
foreach (var deployCompanion in new[] { false, true })
foreach (var createContainers in new[] { false, true }) {
    var values = new Dictionary<string, string>(baseline) {
        ["DEPLOY_STORAGE_NETWORK"] = "true",
        ["DEPLOY_PROXY"] = deployProxy ? "true" : "false",
        ["DEPLOY_COMPANION_APP"] = deployCompanion ? "true" : "false",
        ["CREATE_CONTAINERS"] = createContainers ? "true" : "false",
        ["BLOB_CONTAINERS"] = "other profiles profiles",
        ["CA_BLOB_ROLE"] = "Storage Blob Data Reader"
    };
    var files = DeploymentBicepBundle.Generate(values);
    Check(files["assets/config.json"] == sample && files["assets/seed-profiles.py"] == seedSource, "Bicep bundle includes canonical seed assets");
    Check(files["main.bicep"].Contains("settings.DEPLOY_PROXY ? [containerAppBootstrap.?outputs.identityPrincipalId", StringComparison.Ordinal)
        && files["main.bicep"].Contains("settings.DEPLOY_COMPANION_APP ? [companionAppBootstrap.?outputs.identityPrincipalId", StringComparison.Ordinal), "Bicep grants only selected app principals");
    Check(files["modules/profiles.bicep"].Contains("scope: profiles", StringComparison.Ordinal)
        && !files["modules/profiles.bicep"].Contains("CA_BLOB_ROLE", StringComparison.Ordinal), "profile Contributor does not inherit the optional async role");
    Check(files["modules/blob-storage.bicep"].Contains("settings.CREATE_CONTAINERS ? settings.BLOB_CONTAINERS : []", StringComparison.Ordinal)
        && files["modules/blob-storage.bicep"].Contains("? ['profiles'] : []", StringComparison.Ordinal), "mandatory profiles is independent of optional containers");
    foreach (var stage in new[] { "all", "application" }) {
        var template = JsonNode.Parse(DeploymentArmTemplate.Generate(values, "2.3.0", "2.0.1", new Dictionary<string, string>(), stage))!;
        var deployments = template["resources"]!.AsArray().Where(resource => resource!["type"]!.GetValue<string>() == "Microsoft.Resources/deployments").ToArray();
        var storage = deployments.Single(resource => resource!["name"]!.GetValue<string>().EndsWith("-blob-storage", StringComparison.Ordinal))!;
        var containers = storage["properties"]!["template"]!["resources"]!.AsArray()
            .Where(resource => resource!["type"]!.GetValue<string>().EndsWith("/containers", StringComparison.Ordinal)).ToArray();
        Check(containers.Count(resource => resource!["name"]!.GetValue<string>().EndsWith("/profiles", StringComparison.Ordinal)) == 1, "exactly one profiles container, including CREATE_CONTAINERS=false");
        Check(containers.Length == (createContainers ? 2 : 1), "optional container behavior preserved");
        var profiles = deployments.Single(resource => resource!["name"]!.GetValue<string>().EndsWith("-profiles", StringComparison.Ordinal))!;
        Check(JsonNode.DeepEquals(profiles["properties"]!["template"], compiledProfiles), "Portal reuses the exact compiled canonical Bicep module");
        Check(profiles["properties"]!["parameters"]!["appPrincipalIds"]!["value"]!.AsArray().Count == (deployProxy ? 1 : 0), "Portal profile role follows proxy selection");
    }
    var publicTemplate = DeploymentArmTemplate.GeneratePublicImages(values, DeploymentBicepBundle.ProxyImage, "", new Dictionary<string, string>());
    Check(publicTemplate.Contains("Microsoft.App/jobs", StringComparison.Ordinal), "public-image ARM also schedules the canonical seed job");
}
Console.WriteLine("PASS: mandatory private profiles, selected app roles, canonical assets and staged/public ARM parity (8 selections).");

var pythonChecks = """
import base64, contextlib, io, json, os, urllib.error, urllib.request
from unittest.mock import patch
source = os.environ["SEED_SOURCE"]
payload = base64.b64decode(os.environ["PROFILE_SAMPLE_BASE64"])
blob = os.environ["PROFILE_BLOB_URL"]
for outcomes, expected_exit, expected_calls, expected_text in [
    ([201], 0, 1, "created"),
    ([412], 0, 1, "preserved unchanged"),
    (["already"], 0, 1, "preserved unchanged"),
    ([403, 201], 0, 2, "created"),
    (["dns", 201], 0, 2, "created"),
    ([429, 503, 201], 0, 3, "created"),
    ([403] * 24, 1, 24, "exhausted retries"),
    ([400], 1, 1, "failed"),
    ([409], 1, 1, "failed"),
    (["lease"], 1, 1, "failed"),
]:
    calls = []
    original = b"existing operator-owned profile"
    stored = [original if outcomes in ([412], ["already"]) else None]
    def urlopen(request, timeout):
        assert timeout == 15
        if request.full_url.startswith(os.environ["IDENTITY_ENDPOINT"]):
            assert request.get_header("X-identity-header") == "mock-identity-header"
            assert "api-version=2019-08-01" in request.full_url
            return io.BytesIO(b'{"access_token":"mock-token"}')
        assert request.full_url == blob
        assert request.method == "PUT"
        assert request.get_header("If-none-match") == "*"
        assert request.get_header("Authorization") == "Bearer " + "mock-token"
        assert request.get_header("Content-type") == "application/json"
        assert request.get_header("X-ms-date").endswith(" GMT")
        assert request.data == payload
        calls.append(request)
        outcome = outcomes[len(calls) - 1]
        if outcome == "dns":
            raise urllib.error.URLError("mock DNS propagation")
        if outcome != 201:
            status = 412 if outcome == "lease" else 409 if outcome == "already" else outcome
            code = "LeaseIdMissing" if outcome == "lease" else "BlobAlreadyExists" if outcome == "already" else "ConditionNotMet"
            raise urllib.error.HTTPError(blob, status, "mock", {"x-ms-error-code":code}, None)
        stored[0] = request.data
        response = io.BytesIO(b"")
        response.status = 201
        return response
    output = io.StringIO()
    with patch("urllib.request.urlopen", side_effect=urlopen), patch("time.sleep") as sleep, contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
        try:
            exec(compile(source, "seed-profiles.py", "exec"), {})
            raise AssertionError("seed did not report exit status")
        except SystemExit as result:
            assert result.code == expected_exit, output.getvalue()
    assert len(calls) == expected_calls
    assert sleep.call_count == expected_calls - 1
    assert expected_text in output.getvalue(), output.getvalue()
    assert "mock-token" not in output.getvalue()
    if outcomes in ([412], ["already"]):
        assert stored[0] == original
print("PASS: atomic create/preservation, RBAC/DNS retries, bounded exhaustion and fatal storage errors (10 mocked scenarios).")
""";
var python = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
python.ArgumentList.Add("-c");
python.ArgumentList.Add(pythonChecks);
python.Environment["SEED_SOURCE"] = seedSource;
python.Environment["PROFILE_SAMPLE_BASE64"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sample));
python.Environment["PROFILE_BLOB_URL"] = "https://mock.blob.core.windows.net/profiles/config.json";
python.Environment["STORAGE_AUDIENCE"] = "https://mock.blob.core.windows.net/";
python.Environment["IDENTITY_ENDPOINT"] = "http://localhost/identity";
python.Environment["IDENTITY_HEADER"] = "mock-identity-header";
using (var process = Process.Start(python)!) {
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Check(process.ExitCode == 0, "Python seeder mocks: " + output + error);
    Console.Write(output);
}

var mockDirectory = Path.Combine(root, ".deployment-storage-network-checks-" + Guid.NewGuid().ToString("N"), "bundle with spaces");
Directory.CreateDirectory(mockDirectory);
try {
    var mockAz = """
#!/usr/bin/env bash
set -eu
printf '<%s>' "$@" >> "$MOCK_LOG"
printf '\n' >> "$MOCK_LOG"
if [[ "$1 $2 $3" == 'deployment sub show' ]]; then
    printf '%s\n' '{"proxyUrl":"https://proxy.test","companionAppUrl":"https://companion.test","metricsServerUrl":"","profileSeedJobName":"profiles-mock","profileSeedJobResourceGroup":"rg-storage"}'
elif [[ "$1 $2" == 'cloud show' ]]; then
    if [[ "$*" == *activeDirectoryResourceId* ]]; then
        printf '%s\n' 'https://management.core.windows.net/'
    else
        printf '%s\n' 'https://management.azure.com/'
    fi
elif [[ "$1 $2" == 'account get-access-token' ]]; then
    printf '%s\n' 'mock-access-token'
elif [[ "$1" == rest && "$3" == get ]]; then
    if [[ "$*" == *'/operations/start-profile'* ]]; then
        case "$MOCK_SCENARIO" in
            start-failure) printf '%s\n' '{"status":"Failed"}' ;;
            start-timeout) exit 0 ;;
            start-api-error) exit 17 ;;
            *)
                if [[ -f "$MOCK_LOG.polled" ]]; then
                    printf '%s\n' '{"name":"profiles-mock-execution"}'
                else
                    touch "$MOCK_LOG.polled"
                    printf '%s\n' '{"status":"Running"}'
                fi
                ;;
        esac
        exit 0
    fi
    case "$MOCK_SCENARIO" in
        failure) printf '%s\n' Failed ;;
        timeout) printf '%s\n' Running ;;
        api-error) exit 17 ;;
        *) printf '%s\n' Succeeded ;;
    esac
fi
""";
    var mockCurl = """
#!/usr/bin/env bash
set -eu
printf '<curl>' >> "$MOCK_LOG"
printf '<%s>' "$@" >> "$MOCK_LOG"
printf '\n' >> "$MOCK_LOG"
[[ "$(cat)" == "$(printf '%s: %s %s' 'Authorization' 'Bearer' 'mock-access-token')" ]]
if [[ " $* " != *' --header Content-Length: 0 '* ]]; then
    printf '%s\n' 'curl: (22) The requested URL returned error: 411 (missing Content-Length: 0)' >&2
    exit 22
fi
case "$MOCK_SCENARIO" in
    accepted|start-failure|start-timeout|start-api-error)
        printf 'HTTP/1.1 202 Accepted\r\nLocation: https://management.azure.com/operations/start-profile\r\n\r\n' ;;
    relative-location)
        printf 'HTTP/2 202\r\nlocation: /operations/start-profile\r\n\r\n' ;;
    missing-location) printf 'HTTP/1.1 202 Accepted\r\n\r\n' ;;
    untrusted-location)
        printf 'HTTP/1.1 202 Accepted\r\nLocation: https://management.azure.com.attacker.test/poll\r\n\r\n' ;;
    start-http-error) exit 22 ;;
    empty) printf 'HTTP/1.1 200 OK\r\n\r\n' ;;
    *) printf 'HTTP/1.1 200 OK\r\n\r\n{"name":"profiles-mock-execution"}' ;;
esac
""";
    File.WriteAllText(Path.Combine(mockDirectory, "az"), mockAz + "\n");
    File.WriteAllText(Path.Combine(mockDirectory, "curl"), mockCurl + "\n");
    File.WriteAllText(Path.Combine(mockDirectory, "sleep"), "#!/usr/bin/env bash\nexit 0\n");
    if (!OperatingSystem.IsWindows()) {
        File.SetUnixFileMode(Path.Combine(mockDirectory, "az"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(mockDirectory, "curl"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(Path.Combine(mockDirectory, "sleep"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    foreach (var scenario in new[] { "success", "accepted", "relative-location", "start-failure", "start-timeout", "start-api-error", "missing-location", "untrusted-location", "start-http-error", "failure", "empty", "timeout", "api-error", "disabled", "validate" }) {
        var values = new Dictionary<string, string>(baseline) { ["DEPLOY_STORAGE_NETWORK"] = scenario == "disabled" ? "false" : "true" };
        var files = DeploymentBicepBundle.Generate(values);
        foreach (var file in new[] { "main.bicep", "bootstrap.bicep", "parameters.json", "deploy.sh" })
            File.WriteAllText(Path.Combine(mockDirectory, file), files[file]);
        var log = Path.Combine(mockDirectory, "az.log");
        File.WriteAllText(log, "");
        File.Delete(log + ".polled");
        var bash = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(mockDirectory, "deploy.sh"), "mock-subscription", scenario == "validate" ? "validate" : "create" })
            bash.ArgumentList.Add(argument);
        bash.Environment["PATH"] = mockDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        bash.Environment["MOCK_SCENARIO"] = scenario;
        bash.Environment["MOCK_LOG"] = log;
        using var process = Process.Start(bash)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Check((process.ExitCode == 0) == (scenario is "success" or "accepted" or "relative-location" or "disabled" or "validate"), "bundle tracks seed outcome: " + scenario + "\n" + output + error);
        var calls = File.ReadAllText(log);
        Check(calls.Contains("<--subscription><mock-subscription>", StringComparison.Ordinal), "Azure calls keep subscription argument boundaries");
        Check(calls.Contains("<curl>", StringComparison.Ordinal) == (scenario is not ("disabled" or "validate")), "start only occurs for create with storage networking");
        if (scenario is not ("disabled" or "validate")) {
            Check(calls.Contains("<--header><Content-Length: 0>", StringComparison.Ordinal), "bodyless job-start POST explicitly sends Content-Length: 0");
        }
        Check(!calls.Contains("mock-access-token", StringComparison.Ordinal), "authentication token is not passed as a command argument");
        if (scenario is "success" or "accepted" or "relative-location") {
            Check(output.Contains("Profile seed execution: profiles-mock-execution", StringComparison.Ordinal)
                && output.Contains("Profile seed succeeded", StringComparison.Ordinal), "execution ID and success are observable");
            Check(calls.Contains("/jobs/profiles-mock/executions/profiles-mock-execution?api-version=2024-03-01>", StringComparison.Ordinal), "wait targets the started execution");
        }
        if (scenario is "accepted" or "relative-location") {
            Check(Regex.Matches(calls, "<--url><https://management.azure.com/operations/start-profile>").Count == 2, "202 start polls Location until the execution is returned");
        }
        if (scenario is "missing-location" or "untrusted-location" or "start-failure" or "start-timeout" or "start-api-error" or "start-http-error") {
            Check(!calls.Contains("/executions/", StringComparison.Ordinal), "invalid or incomplete starts never poll an execution");
        }
    }
    Console.WriteLine("PASS: bundle HTTP 200/202 start and Location polling, start/execution failures, bounded timeouts, API errors, disabled mode and validation (15 mocked scenarios).");
} finally {
    Directory.Delete(Path.GetDirectoryName(mockDirectory)!, true);
}

static void Check(bool result, string description) {
    if (!result) throw new InvalidOperationException("Failed: " + description);
}
