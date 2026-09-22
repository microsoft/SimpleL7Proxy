using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CompanionApp.Components.Pages;

/// <summary>Packages static Bicep assets with deployment-specific parameters.</summary>
public static class DeploymentBicepBundle {
    private const string AssetPrefix = "DeploymentBicepAsset/";

    /// <summary>The anonymously pullable SimpleL7Proxy v2.3.0 release, pinned by digest.</summary>
    public const string ProxyImage = "publicnvmacr.azurecr.io/simplel7proxy@sha256:2ebaff3e90fc9162421f08f8095a030627c4aea7046b3da59a8ea7720e4c530f";

    /// <summary>The anonymously pullable HealthProbe v2.0.1 release, pinned by digest.</summary>
    public const string HealthProbeImage = "publicnvmacr.azurecr.io/healthprobe@sha256:e28a0bd8555d97ec800f80cb37201785e4ceb9c783fbedf679de5145e3c689d1";

    /// <summary>The anonymously pullable Companion App v2.3.0 release.</summary>
    public const string CompanionImage = "publicnvmacr.azurecr.io/companionapp:v2.3.0";

    /// <summary>The anonymously pullable Metrics Server v1.0.0 release.</summary>
    public const string MetricsServerImage = "publicnvmacr.azurecr.io/metricsserver:v1.0.0";

    /// <summary>Packages static Bicep templates with the selected deployment values.</summary>
    public static IReadOnlyDictionary<string, string> Generate(IReadOnlyDictionary<string, string> values) {
        ArgumentNullException.ThrowIfNull(values);
        Validate(values);

        var files = LoadTemplateAssets();
        files.Add("parameters.json", GenerateParameters(values));
        files.Add("deploy.sh", GenerateScript(values));
        using var readmeStream = typeof(DeploymentBicepBundle).Assembly.GetManifestResourceStream("DeploymentReadmeTemplate")
            ?? throw new InvalidOperationException("The deployment README template is missing.");
        using var readmeReader = new StreamReader(readmeStream);
        files.Add("README.md", readmeReader.ReadToEnd()
            .Replace("{{DEPLOYMENT_NAME}}", ShellString(values["CONTAINER_APP_NAME"] + "-bicep"), StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal));
        return files;
    }

    private static SortedDictionary<string, string> LoadTemplateAssets() {
        var assembly = typeof(DeploymentBicepBundle).Assembly;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith(AssetPrefix, StringComparison.Ordinal))) {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The deployment template asset {resourceName} is missing.");
            using var reader = new StreamReader(stream);
            var path = resourceName[AssetPrefix.Length..].Replace('\\', '/');
            files.Add(path, reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        if (files.Count == 0) throw new InvalidOperationException("The Bicep deployment template assets are missing.");
        return files;
    }

    private static string GenerateParameters(IReadOnlyDictionary<string, string> values) {
        var resourceGroupKeys = new[] { "CONTAINER_APP_RESOURCE_GROUP", "APPCONFIG_RESOURCE_GROUP" }.ToList();
        if (Enabled(values, "DEPLOY_COMPANION_APP")) resourceGroupKeys.Add("COMPANION_APP_RESOURCE_GROUP");
        if (Enabled(values, "PRIVATE_NETWORK_DEPLOYMENT")) resourceGroupKeys.Add("NETWORK_RESOURCE_GROUP");
        if (Enabled(values, "ASYNC_DEPLOYMENT")) resourceGroupKeys.AddRange(["STORAGE_RESOURCE_GROUP", "REQUESTAPI_RESOURCE_GROUP"]);
        var resourceGroups = new JsonArray();
        foreach (var group in resourceGroupKeys.Select(key => Value(values, key)).Distinct(StringComparer.OrdinalIgnoreCase)) resourceGroups.Add(group);
        var containers = new JsonArray();
        foreach (var container in Value(values, "BLOB_CONTAINERS").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)) containers.Add(container);

        var settings = new JsonObject {
            ["PRIVATE_NETWORK_DEPLOYMENT"] = Enabled(values, "PRIVATE_NETWORK_DEPLOYMENT"),
            ["ASYNC_DEPLOYMENT"] = Enabled(values, "ASYNC_DEPLOYMENT"),
            ["DEPLOY_COMPANION_APP"] = Enabled(values, "DEPLOY_COMPANION_APP"),
            ["DEPLOY_METRICS_SERVER"] = Enabled(values, "DEPLOY_METRICS_SERVER"),
            ["MAKE_UNIQ_SUFFIX"] = "",
            ["LOCATION"] = Value(values, "LOCATION"),
            ["RESOURCE_GROUPS"] = resourceGroups,
            ["NETWORK_RESOURCE_GROUP"] = Value(values, "NETWORK_RESOURCE_GROUP"),
            ["CONTAINER_APP_RESOURCE_GROUP"] = Value(values, "CONTAINER_APP_RESOURCE_GROUP"),
            ["STORAGE_RESOURCE_GROUP"] = Value(values, "STORAGE_RESOURCE_GROUP"),
            ["APPCONFIG_RESOURCE_GROUP"] = Value(values, "APPCONFIG_RESOURCE_GROUP"),
            ["REQUESTAPI_RESOURCE_GROUP"] = Value(values, "REQUESTAPI_RESOURCE_GROUP"),
            ["SERVICEBUS_RESOURCE_GROUP"] = OptionalValue(values, "SERVICEBUS_RESOURCE_GROUP", Value(values, "REQUESTAPI_RESOURCE_GROUP")),
            ["COSMOS_RESOURCE_GROUP"] = OptionalValue(values, "COSMOS_RESOURCE_GROUP", Value(values, "REQUESTAPI_RESOURCE_GROUP")),
            ["COMPANION_APP_RESOURCE_GROUP"] = Value(values, "COMPANION_APP_RESOURCE_GROUP"),
            ["ACR_NAME"] = Value(values, "ACR_NAME"),
            ["ACR_SKU"] = Value(values, "ACR_SKU"),
            ["PROXY_IMAGE_NAME"] = Value(values, "PROXY_IMAGE_NAME"),
            ["HEALTH_IMAGE_NAME"] = Value(values, "HEALTH_IMAGE_NAME"),
            ["COMPANION_IMAGE_NAME"] = Value(values, "COMPANION_IMAGE_NAME"),
            ["CONTAINER_APP_NAME"] = Value(values, "CONTAINER_APP_NAME"),
            ["COMPANION_APP_NAME"] = Value(values, "COMPANION_APP_NAME"),
            ["METRICS_SERVER_NAME"] = Value(values, "METRICS_SERVER_NAME"),
            ["MIN_REPLICAS"] = Integer(values, "MIN_REPLICAS"),
            ["MAX_REPLICAS"] = Integer(values, "MAX_REPLICAS"),
            ["ENABLE_MANAGED_IDENTITY"] = Enabled(values, "ENABLE_MANAGED_IDENTITY"),
            ["ENABLE_APP_INSIGHTS"] = Enabled(values, "ENABLE_APP_INSIGHTS"),
            ["LOG_ANALYTICS_WORKSPACE_NAME"] = Value(values, "LOG_ANALYTICS_WORKSPACE_NAME"),
            ["USE_EXISTING_ENVIRONMENT"] = Enabled(values, "USE_EXISTING_ENVIRONMENT"),
            ["ENVIRONMENT_RESOURCE_GROUP"] = Value(values, "ENVIRONMENT_RESOURCE_GROUP"),
            ["ENVIRONMENT_NAME"] = Value(values, "ENVIRONMENT_NAME"),
            ["WEB_CPU"] = Value(values, "WEB_CPU"),
            ["WEB_MEMORY"] = Value(values, "WEB_MEMORY"),
            ["HEALTH_CPU"] = Value(values, "HEALTH_CPU"),
            ["HEALTH_MEMORY"] = Value(values, "HEALTH_MEMORY"),
            ["METRICS_CPU"] = Value(values, "METRICS_CPU"),
            ["METRICS_MEMORY"] = Value(values, "METRICS_MEMORY"),
            ["WEB_PORT"] = Integer(values, "WEB_PORT"),
            ["HEALTH_PORT"] = Integer(values, "HEALTH_PORT"),
            ["INGRESS_TYPE"] = Value(values, "INGRESS_TYPE"),
            ["ENABLE_HTTPS"] = Enabled(values, "ENABLE_HTTPS"),
            ["REVISION_MODE"] = Value(values, "REVISION_MODE"),
            ["TERMINATION_GRACE_PERIOD_SECONDS"] = Integer(values, "TERMINATION_GRACE_PERIOD_SECONDS"),
            ["HEALTHPROBE_TYPE"] = Value(values, "HEALTHPROBE_TYPE"),
            ["APPCONFIG_NAME"] = Value(values, "APPCONFIG_NAME"),
            ["APPCONFIG_SKU"] = Value(values, "APPCONFIG_SKU"),
            ["APPCONFIG_LABEL"] = Value(values, "APPCONFIG_LABEL"),
            ["AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS"] = Integer(values, "AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS"),
            ["UPDATE_CONTAINER_APP_ENV"] = Enabled(values, "UPDATE_CONTAINER_APP_ENV"),
            ["VNET_NAME"] = Value(values, "VNET_NAME"),
            ["VNET_ADDRESS_PREFIX"] = Value(values, "VNET_ADDRESS_PREFIX"),
            ["SUBNET_ACA_NAME"] = Value(values, "SUBNET_ACA_NAME"),
            ["SUBNET_ACA_PREFIX"] = Value(values, "SUBNET_ACA_PREFIX"),
            ["SUBNET_CLIENTVM_NAME"] = Value(values, "SUBNET_CLIENTVM_NAME"),
            ["SUBNET_CLIENTVM_PREFIX"] = Value(values, "SUBNET_CLIENTVM_PREFIX"),
            ["SUBNET_AZUREFUNCTIONS_NAME"] = Value(values, "SUBNET_AZUREFUNCTIONS_NAME"),
            ["SUBNET_AZUREFUNCTIONS_PREFIX"] = Value(values, "SUBNET_AZUREFUNCTIONS_PREFIX"),
            ["SUBNET_APIM_NAME"] = Value(values, "SUBNET_APIM_NAME"),
            ["SUBNET_APIM_PREFIX"] = Value(values, "SUBNET_APIM_PREFIX"),
            ["SUBNET_PRIVATEENDPOINTS_NAME"] = Value(values, "SUBNET_PRIVATEENDPOINTS_NAME"),
            ["SUBNET_PRIVATEENDPOINTS_PREFIX"] = Value(values, "SUBNET_PRIVATEENDPOINTS_PREFIX"),
            ["DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES"] = Enabled(values, "DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES"),
            ["DNS_ZONE_NAME"] = Value(values, "DNS_ZONE_NAME"),
            ["ACA_INTERNAL_FQDN"] = Value(values, "ACA_INTERNAL_FQDN"),
            ["ACA_RECORD_NAME"] = Value(values, "ACA_RECORD_NAME"),
            ["APIM_PRIVATE_IP"] = Value(values, "APIM_PRIVATE_IP"),
            ["APIM_RECORD_NAME"] = Value(values, "APIM_RECORD_NAME"),
            ["STORAGE_ACCOUNT_NAME"] = Value(values, "STORAGE_ACCOUNT_NAME"),
            ["STORAGE_SKU"] = Value(values, "STORAGE_SKU"),
            ["CREATE_CONTAINERS"] = Enabled(values, "CREATE_CONTAINERS"),
            ["BLOB_CONTAINERS"] = containers,
            ["CA_BLOB_ROLE"] = Value(values, "CA_BLOB_ROLE"),
            ["REQUESTAPI_FUNCTION_APP"] = Value(values, "REQUESTAPI_FUNCTION_APP"),
            ["REQUESTAPI_LOCATION"] = OptionalValue(values, "REQUESTAPI_LOCATION", Value(values, "LOCATION")),
            ["REQUESTAPI_STORAGE_ACCOUNT"] = Value(values, "REQUESTAPI_STORAGE_ACCOUNT"),
            ["REQUESTAPI_APPINSIGHTS_NAME"] = Value(values, "REQUESTAPI_APPINSIGHTS_NAME"),
            ["REQUESTAPI_RUNTIME_NAME"] = Value(values, "REQUESTAPI_RUNTIME_NAME"),
            ["REQUESTAPI_RUNTIME_VERSION"] = Value(values, "REQUESTAPI_RUNTIME_VERSION"),
            ["REQUESTAPI_INSTANCE_MEMORY_MB"] = Integer(values, "REQUESTAPI_INSTANCE_MEMORY_MB"),
            ["REQUESTAPI_MAX_INSTANCE_COUNT"] = Integer(values, "REQUESTAPI_MAX_INSTANCE_COUNT"),
            ["REQUESTAPI_SERVICEBUS_NAMESPACE"] = Value(values, "REQUESTAPI_SERVICEBUS_NAMESPACE"),
            ["REQUESTAPI_SERVICEBUS_QUEUE"] = Value(values, "REQUESTAPI_SERVICEBUS_QUEUE"),
            ["REQUESTAPI_SERVICEBUS_FEEDER_QUEUE"] = Value(values, "REQUESTAPI_SERVICEBUS_FEEDER_QUEUE"),
            ["REQUESTAPI_COSMOS_ACCOUNT"] = Value(values, "REQUESTAPI_COSMOS_ACCOUNT"),
            ["REQUESTAPI_COSMOS_DATABASE"] = Value(values, "REQUESTAPI_COSMOS_DATABASE"),
            ["REQUESTAPI_COSMOS_CONTAINER"] = Value(values, "REQUESTAPI_COSMOS_CONTAINER")
        };
        var document = new JsonObject {
            ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
            ["contentVersion"] = "1.0.0.0",
            ["parameters"] = new JsonObject {
                ["settings"] = new JsonObject { ["value"] = settings }
            }
        };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static void Validate(IReadOnlyDictionary<string, string> values) {
        if (!Enabled(values, "ENABLE_MANAGED_IDENTITY")) {
            throw new InvalidOperationException("Enable managed identity before exporting Bicep: registry, App Configuration, and storage access use managed identity.");
        }
        if (Enabled(values, "ASYNC_DEPLOYMENT")) {
            var maximumInstances = Integer(values, "REQUESTAPI_MAX_INSTANCE_COUNT");
            if (maximumInstances is < 40 or > 1000) throw new InvalidOperationException("RequestAPI maximum instance count must be between 40 and 1000 for Flex Consumption.");
            if (Enabled(values, "PRIVATE_NETWORK_DEPLOYMENT") && OptionalValue(values, "REQUESTAPI_LOCATION", Value(values, "LOCATION")) != Value(values, "LOCATION")) {
                throw new InvalidOperationException("RequestAPI must use the common location when integrated with the selected virtual network.");
            }
            var blobRole = Value(values, "CA_BLOB_ROLE");
            if (blobRole is not ("Storage Blob Data Contributor" or "Storage Blob Data Owner" or "Storage Blob Data Reader") && !Guid.TryParse(blobRole, out _)) {
                throw new InvalidOperationException("For Bicep export, enter a built-in Storage Blob Data role name or a role definition GUID in CA_BLOB_ROLE.");
            }
        }
    }

    /// <summary>Creates a deterministic ZIP from generated deployment files.</summary>
    public static byte[] CreateArchive(IReadOnlyDictionary<string, string> files) {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) {
            foreach (var file in files.OrderBy(file => file.Key, StringComparer.Ordinal)) {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (file.Key == "deploy.sh") entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Value);
            }
        }
        return output.ToArray();
    }

    private static string GenerateScript(IReadOnlyDictionary<string, string> values) {
        using var scriptStream = typeof(DeploymentBicepBundle).Assembly.GetManifestResourceStream("DeploymentScriptTemplate")
            ?? throw new InvalidOperationException("The deployment script template is missing.");
        using var scriptReader = new StreamReader(scriptStream);
        return scriptReader.ReadToEnd()
            .Replace("{{DEPLOYMENT_NAME}}", ShellString(values["CONTAINER_APP_NAME"] + "-bicep"), StringComparison.Ordinal)
            .Replace("{{BOOTSTRAP_DEPLOYMENT_NAME}}", ShellString(values["CONTAINER_APP_NAME"] + "-bicep-bootstrap"), StringComparison.Ordinal)
            .Replace("{{LOCATION}}", ShellString(values["LOCATION"]), StringComparison.Ordinal)
            .Replace("{{ACR_NAME}}", ShellString(values["ACR_NAME"]), StringComparison.Ordinal)
            .Replace("{{ACR_RESOURCE_GROUP}}", ShellString(values["CONTAINER_APP_RESOURCE_GROUP"]), StringComparison.Ordinal)
            .Replace("{{PROXY_SOURCE_IMAGE}}", ShellString(ProxyImage), StringComparison.Ordinal)
            .Replace("{{PROXY_TARGET_IMAGE}}", ShellString(values["PROXY_IMAGE_NAME"] + ":v2.3.0"), StringComparison.Ordinal)
            .Replace("{{HEALTH_IMAGE_IMPORT}}", values["HEALTHPROBE_TYPE"] == "sidecar"
                ? $"az acr import --subscription \"$subscription\" --resource-group \"$acr_resource_group\" --name \"$acr_name\" --source {ShellString(HealthProbeImage)} --image {ShellString(values["HEALTH_IMAGE_NAME"] + ":v2.0.1")} --force"
                : string.Empty, StringComparison.Ordinal)
            .Replace("{{COMPANION_IMAGE_IMPORT}}", Enabled(values, "DEPLOY_COMPANION_APP")
                ? $"az acr import --subscription \"$subscription\" --resource-group \"$acr_resource_group\" --name \"$acr_name\" --source {ShellString(CompanionImage)} --image {ShellString(values["COMPANION_IMAGE_NAME"] + ":v2.3.0")} --force"
                : string.Empty, StringComparison.Ordinal)
            .Replace("{{METRICS_IMAGE_IMPORT}}", Enabled(values, "DEPLOY_METRICS_SERVER")
                ? $"az acr import --subscription \"$subscription\" --resource-group \"$acr_resource_group\" --name \"$acr_name\" --source {ShellString(MetricsServerImage)} --image 'metricsserver:v1.0.0' --force"
                : string.Empty, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var value)
        ? value : throw new InvalidOperationException($"The deployment value {key} is missing.");
    private static string OptionalValue(IReadOnlyDictionary<string, string> values, string key, string fallback) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    private static bool Enabled(IReadOnlyDictionary<string, string> values, string key) => Value(values, key) is "true" or "yes";
    private static int Integer(IReadOnlyDictionary<string, string> values, string key) => int.Parse(Value(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static string ShellString(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}