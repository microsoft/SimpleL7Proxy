using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace chat_tester.Components.Pages;

/// <summary>Builds a self-contained ARM deployment from Deployment Setup values.</summary>
public sealed class DeploymentArmTemplate {
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly JsonArray _deployments = [];
    private readonly Dictionary<string, string> _deploymentIds = new(StringComparer.Ordinal);
    private readonly string _location;
    private readonly string _stage;

    private DeploymentArmTemplate(IReadOnlyDictionary<string, string> values, string stage) {
        _values = values;
        _location = Value("LOCATION");
        _stage = stage;
    }

    /// <summary>Creates a portal-loadable template without contacting Azure.</summary>
    public static string Generate(IReadOnlyDictionary<string, string> values, string proxyVersion, string healthVersion, IReadOnlyDictionary<string, string> defaults, string stage = "all") {
        if (stage is not ("all" or "bootstrap" or "application")) throw new ArgumentException("Unknown deployment stage.", nameof(stage));
        var builder = new DeploymentArmTemplate(values, stage);
        return builder.Build(proxyVersion, healthVersion, defaults).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private JsonObject Build(string proxyVersion, string healthVersion, IReadOnlyDictionary<string, string> defaults) {
        if (!Enabled("ENABLE_MANAGED_IDENTITY")) {
            throw new InvalidOperationException("Enable managed identity before exporting ARM: registry, App Configuration, and storage access use managed identity.");
        }
        var groups = new[] { "CONTAINER_APP_RESOURCE_GROUP", "APPCONFIG_RESOURCE_GROUP" }.ToList();
        if (Enabled("PRIVATE_NETWORK_DEPLOYMENT")) groups.Add("NETWORK_RESOURCE_GROUP");
        if (Enabled("ASYNC_DEPLOYMENT")) groups.AddRange(["STORAGE_RESOURCE_GROUP", "REQUESTAPI_RESOURCE_GROUP"]);
        var resourceGroups = new JsonArray();
        foreach (var group in groups.Select(Value).Distinct(StringComparer.OrdinalIgnoreCase).Where(_ => _stage != "application")) {
            resourceGroups.Add(new JsonObject {
                ["type"] = "Microsoft.Resources/resourceGroups", ["apiVersion"] = "2022-09-01",
                ["name"] = Literal(group), ["location"] = _location
            });
        }

        if (_stage != "application") {
            var registry = Resource("Microsoft.ContainerRegistry/registries", "2023-07-01", Value("ACR_NAME"), new JsonObject {
                ["adminUserEnabled"] = false, ["publicNetworkAccess"] = "Enabled"
            }, _location);
            registry["sku"] = new JsonObject { ["name"] = Value("ACR_SKU") };
            AddDeployment("registry", Value("CONTAINER_APP_RESOURCE_GROUP"), new JsonArray(registry));
        }
        if (_stage != "bootstrap") {
            if (Enabled("PRIVATE_NETWORK_DEPLOYMENT")) AddNetwork();
            AddFoundation();
            if (Enabled("ASYNC_DEPLOYMENT")) AddAsync();
            AddConfigurationValues(defaults);
            AddContainerApp(proxyVersion, healthVersion);
            if (Enabled("PRIVATE_NETWORK_DEPLOYMENT")) AddDns();
        }
        foreach (var deployment in _deployments) resourceGroups.Add(deployment!.DeepClone());
        var template = Template(resourceGroups);
        template["$schema"] = "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#";
        if (_stage != "bootstrap" && Enabled("ASYNC_DEPLOYMENT")) template["parameters"] = ExternalGroupParameters();
        template["metadata"] = new JsonObject {
            ["stage"] = _stage,
            ["description"] = _stage == "application"
                ? "SimpleL7Proxy application resources. Deploy through Azure portal Custom deployment after bootstrap and image publishing, in the same subscription. Existing resource groups and ACR are not recreated."
                : "SimpleL7Proxy subscription-scoped infrastructure deployment. Resource groups are created at the root with direct dependencies from embedded service templates. Deploy through Azure portal Custom deployment.",
            ["prerequisites"] = _stage == "bootstrap" ? "Deploy first with resource-group and registry creation permissions. Then publish images before deploying the application template."
                : "Resource groups and ACR must exist for the application stage. Publish the selected container images before application deployment. RequestAPI application code is published separately. Existing Service Bus and Cosmos DB dependencies are not created. The deployer needs role-assignment permissions and App Configuration Data Owner on the target group or subscription.",
            ["imageBuild"] = new JsonObject { ["method"] = Value("BUILD_METHOD"), ["dockerfile"] = Literal(Value("DOCKERFILE_PATH")), ["execution"] = "External prerequisite; ARM does not build local source code." }
        };
        return template;
    }

    private void AddFoundation() {
        var appGroup = Value("CONTAINER_APP_RESOURCE_GROUP");
        var identityName = Value("CONTAINER_APP_NAME") + "-identity";
        var identityId = Id(appGroup, "Microsoft.ManagedIdentity/userAssignedIdentities", identityName);
        var registryId = Id(appGroup, "Microsoft.ContainerRegistry/registries", Value("ACR_NAME"));
        JsonArray resources = [
            Resource("Microsoft.ManagedIdentity/userAssignedIdentities", "2023-01-31", identityName, new JsonObject(), _location)
        ];
        resources.Add(Role(registryId, identityId, "7f951dda-4ed3-4680-a7ca-43fe172d538d", true));
        resources[1]!["dependsOn"] = Strings($"[{identityId}]");
        var environment = Resource("Microsoft.App/managedEnvironments", "2024-03-01", Value("ENVIRONMENT_NAME"), new JsonObject {
            ["workloadProfiles"] = new JsonArray(new JsonObject { ["name"] = "Consumption", ["workloadProfileType"] = "Consumption" })
        }, _location);
        if (Enabled("ENABLE_APP_INSIGHTS")) {
            var workspace = Resource("Microsoft.OperationalInsights/workspaces", "2023-09-01", Value("LOG_ANALYTICS_WORKSPACE_NAME"), new JsonObject {
                ["sku"] = new JsonObject { ["name"] = "PerGB2018" }, ["retentionInDays"] = 30
            }, _location);
            resources.Add(workspace);
            var workspaceId = Id(appGroup, "Microsoft.OperationalInsights/workspaces", Value("LOG_ANALYTICS_WORKSPACE_NAME"));
            environment["properties"]!["appLogsConfiguration"] = new JsonObject {
                ["destination"] = "log-analytics", ["logAnalyticsConfiguration"] = new JsonObject {
                    ["customerId"] = $"[reference({workspaceId}, '2023-09-01').customerId]",
                    ["sharedKey"] = $"[listKeys({workspaceId}, '2023-09-01').primarySharedKey]"
                }
            };
            environment["dependsOn"] = Strings($"[{workspaceId}]");
            var insights = Resource("Microsoft.Insights/components", "2020-02-02", Value("CONTAINER_APP_NAME") + "-insights", new JsonObject {
                ["Application_Type"] = "web", ["WorkspaceResourceId"] = $"[{workspaceId}]"
            }, _location);
            insights["kind"] = "web";
            insights["dependsOn"] = Strings($"[{workspaceId}]");
            resources.Add(insights);
        }
        if (Enabled("PRIVATE_NETWORK_DEPLOYMENT")) {
            environment["properties"]!["vnetConfiguration"] = new JsonObject {
                ["infrastructureSubnetId"] = $"[{Id(Value("NETWORK_RESOURCE_GROUP"), "Microsoft.Network/virtualNetworks/subnets", Value("VNET_NAME"), Value("SUBNET_ACA_NAME"))}]",
                ["internal"] = true
            };
        }
        resources.Add(environment);
        AddDeployment("foundation", appGroup, resources, [..(_stage == "all" ? new[] { "registry" } : []), ..(Enabled("PRIVATE_NETWORK_DEPLOYMENT") ? new[] { "network" } : [])]);
        var config = Resource("Microsoft.AppConfiguration/configurationStores", "2024-05-01", Value("APPCONFIG_NAME"), new JsonObject {
            ["disableLocalAuth"] = true, ["publicNetworkAccess"] = "Enabled",
            ["dataPlaneProxy"] = new JsonObject { ["authenticationMode"] = "Pass-through" }
        }, _location);
        config["sku"] = new JsonObject { ["name"] = Value("APPCONFIG_SKU") };
        var configId = Id(Value("APPCONFIG_RESOURCE_GROUP"), "Microsoft.AppConfiguration/configurationStores", Value("APPCONFIG_NAME"));
        AddDeployment("configuration", Value("APPCONFIG_RESOURCE_GROUP"), new JsonArray(config,
            Role(configId, identityId, "516239f1-63e1-4d78-a4de-a74fb236a071", false)), "foundation");
    }

    private void AddContainerApp(string proxyVersion, string healthVersion) {
        var appGroup = Value("CONTAINER_APP_RESOURCE_GROUP");
        var identityId = Id(appGroup, "Microsoft.ManagedIdentity/userAssignedIdentities", Value("CONTAINER_APP_NAME") + "-identity");
        var configId = Id(Value("APPCONFIG_RESOURCE_GROUP"), "Microsoft.AppConfiguration/configurationStores", Value("APPCONFIG_NAME"));
        var registryId = Id(appGroup, "Microsoft.ContainerRegistry/registries", Value("ACR_NAME"));
        var registry = $"reference({registryId}, '2023-07-01').loginServer";
        var proxyTag = string.IsNullOrWhiteSpace(Value("PROXY_VERSION_OVERRIDE")) ? proxyVersion : Value("PROXY_VERSION_OVERRIDE");
        var healthTag = string.IsNullOrWhiteSpace(Value("HEALTHPROBE_VERSION_OVERRIDE")) ? healthVersion : Value("HEALTHPROBE_VERSION_OVERRIDE");
        if (!proxyTag.StartsWith('v')) proxyTag = "v" + proxyTag;
        if (!healthTag.StartsWith('v')) healthTag = "v" + healthTag;
        JsonArray env = [
            Setting("Host1", Literal(Value("HOST1"))), Setting("HealthProbeSidecar", SidecarSetting()),
            Setting("Port", Value("WEB_PORT")), Setting("AZURE_CLIENT_ID", $"[reference({identityId}, '2023-01-31').clientId]")
        ];
        foreach (var setting in RuntimeConnections()) env.Add(Setting(setting.Key, setting.Value));
        if (Enabled("UPDATE_CONTAINER_APP_ENV")) {
            env.Add(Setting("AZURE_APPCONFIG_ENDPOINT", $"[reference({configId}, '2024-05-01').endpoint]"));
            env.Add(Setting("AZURE_APPCONFIG_LABEL", Literal(Value("APPCONFIG_LABEL"))));
            env.Add(Setting("AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS", Value("AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS")));
        }
        JsonArray containers = [new JsonObject {
            ["name"] = "proxy", ["image"] = $"[concat({registry}, '/', {Quote(Value("PROXY_IMAGE_NAME"))}, ':', {Quote(proxyTag)})]",
            ["env"] = env, ["resources"] = new JsonObject { ["cpu"] = Number("WEB_CPU"), ["memory"] = Value("WEB_MEMORY") + "Gi" }
        }];
        if (Value("HEALTHPROBE_TYPE") == "sidecar") {
            containers.Add(new JsonObject {
                ["name"] = "health", ["image"] = $"[concat({registry}, '/', {Quote(Value("HEALTH_IMAGE_NAME"))}, ':', {Quote(healthTag)})]",
                ["env"] = new JsonArray(Setting("HEALTHPROBE_PORT", Value("HEALTH_PORT"))),
                ["resources"] = new JsonObject { ["cpu"] = Number("HEALTH_CPU"), ["memory"] = Value("HEALTH_MEMORY") + "Gi" },
                ["probes"] = new JsonArray(Probe("Liveness", "/liveness"), Probe("Readiness", "/readiness"), Probe("Startup", "/startup"))
            });
        }
        var app = Resource("Microsoft.App/containerApps", "2024-03-01", Value("CONTAINER_APP_NAME"), new JsonObject {
            ["managedEnvironmentId"] = $"[{Id(appGroup, "Microsoft.App/managedEnvironments", Value("ENVIRONMENT_NAME"))}]",
            ["workloadProfileName"] = "Consumption",
            ["configuration"] = new JsonObject {
                ["activeRevisionsMode"] = Value("REVISION_MODE"),
                ["registries"] = new JsonArray(new JsonObject { ["server"] = $"[{registry}]", ["identity"] = $"[{identityId}]" }),
                ["ingress"] = new JsonObject {
                    ["external"] = Value("INGRESS_TYPE") == "external", ["targetPort"] = Integer("WEB_PORT"),
                    ["transport"] = "auto", ["allowInsecure"] = !Enabled("ENABLE_HTTPS"),
                    ["traffic"] = new JsonArray(new JsonObject { ["latestRevision"] = true, ["weight"] = 100 })
                }
            },
            ["template"] = new JsonObject {
                ["terminationGracePeriodSeconds"] = Integer("TERMINATION_GRACE_PERIOD_SECONDS"), ["containers"] = containers,
                ["scale"] = new JsonObject {
                    ["minReplicas"] = Integer("MIN_REPLICAS"), ["maxReplicas"] = Integer("MAX_REPLICAS"),
                    ["rules"] = new JsonArray(new JsonObject {
                        ["name"] = "http-scaling", ["http"] = new JsonObject { ["metadata"] = new JsonObject { ["concurrentRequests"] = "1000" } }
                    })
                }
            }
        }, _location);
        app["identity"] = new JsonObject {
            ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [$"[{identityId}]"] = new JsonObject() }
        };
        AddDeployment("container-app", appGroup, new JsonArray(app), "foundation", "configuration-values");
    }

    private string SidecarSetting() => $"Enabled={(Value("HEALTHPROBE_TYPE") == "sidecar" ? "true" : "false")};url=http://localhost:{Value("HEALTH_PORT")}";

    private Dictionary<string, string> RuntimeConnections() {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Enabled("ENABLE_APP_INSIGHTS")) {
            var insightsId = Id(Value("CONTAINER_APP_RESOURCE_GROUP"), "Microsoft.Insights/components", Value("CONTAINER_APP_NAME") + "-insights");
            settings["APPINSIGHTS_CONNECTIONSTRING"] = $"[reference({insightsId}, '2020-02-02').ConnectionString]";
        }
        if (Enabled("ASYNC_DEPLOYMENT")) {
            var blobId = Id(Value("STORAGE_RESOURCE_GROUP"), "Microsoft.Storage/storageAccounts", Value("STORAGE_ACCOUNT_NAME"));
            var functionId = Id(Value("REQUESTAPI_RESOURCE_GROUP"), "Microsoft.Web/sites", Value("REQUESTAPI_FUNCTION_APP"));
            settings["AsyncModeEnabled"] = "true";
            settings["AsyncBlobStorageConfig"] = $"[concat('uri=', reference({blobId}, '2023-05-01').primaryEndpoints.blob, ',mi=true')]";
            settings["AsyncSBConfig"] = $"ns={Value("REQUESTAPI_SERVICEBUS_NAMESPACE")},q={Value("REQUESTAPI_SERVICEBUS_QUEUE")},mi=true";
            settings["RequestAPIBaseUri"] = $"[concat('https://', reference({functionId}, '2024-04-01').defaultHostName, '/api/')]";
        }
        return settings;
    }

    private void AddConfigurationValues(IReadOnlyDictionary<string, string> defaults) {
        var values = defaults.ToDictionary(entry => entry.Key, entry => Literal(entry.Value), StringComparer.Ordinal);
        values["Warm:Host1"] = Literal(Value("HOST1"));
        values["Warm:HealthProbe:Sidecar"] = SidecarSetting();
        values["Cold:Server:Port"] = Value("WEB_PORT");
        values["Warm:Sentinel"] = "1";
        values["RefreshSeconds"] = Value("AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS");
        foreach (var setting in RuntimeConnections()) {
            var key = setting.Key switch {
                "APPINSIGHTS_CONNECTIONSTRING" => "Cold:Logging:AppInsightsConnectionString",
                "AsyncModeEnabled" => "Cold:Async:Enabled",
                "AsyncBlobStorageConfig" => "Cold:Async:Storage:BlobConfig",
                "AsyncSBConfig" => "Cold:Async:ServiceBus:Config",
                "RequestAPIBaseUri" => "Cold:Async:RequestAPIBaseUri",
                _ => throw new InvalidOperationException("Unmapped runtime configuration setting.")
            };
            values[key] = setting.Value;
        }
        var resources = new JsonArray();
        foreach (var entry in values.OrderBy(entry => entry.Key, StringComparer.Ordinal)) {
            var key = Uri.EscapeDataString(entry.Key).Replace("~", "~7E", StringComparison.Ordinal).Replace('%', '~');
            var label = Uri.EscapeDataString(Value("APPCONFIG_LABEL")).Replace("~", "~7E", StringComparison.Ordinal).Replace('%', '~');
            resources.Add(Resource("Microsoft.AppConfiguration/configurationStores/keyValues", "2024-05-01", Value("APPCONFIG_NAME") + "/" + key + "$" + label,
                new JsonObject { ["value"] = entry.Value, ["contentType"] = "text/plain" }));
        }
        AddDeployment("configuration-values", Value("APPCONFIG_RESOURCE_GROUP"), resources,
            Enabled("ASYNC_DEPLOYMENT") ? ["configuration", "blob-storage", "request-api", "service-bus-access", "cosmos-access"] : ["configuration"]);
    }

    private JsonObject Probe(string type, string path) => new() {
        ["type"] = type, ["httpGet"] = new JsonObject { ["path"] = path, ["port"] = Integer("HEALTH_PORT") },
        ["initialDelaySeconds"] = 5, ["periodSeconds"] = 10, ["timeoutSeconds"] = 10,
        ["successThreshold"] = 1, ["failureThreshold"] = 30
    };

    private void AddNetwork() {
        var subnets = new JsonArray();
        foreach (var prefix in new[] { "ACA", "CLIENTVM", "AZUREFUNCTIONS", "APIM", "PRIVATEENDPOINTS" }) {
            var properties = new JsonObject { ["addressPrefix"] = Value($"SUBNET_{prefix}_PREFIX") };
            var delegation = prefix == "ACA" ? "Microsoft.App/environments"
                : prefix == "AZUREFUNCTIONS" && Enabled("ASYNC_DEPLOYMENT") ? "Microsoft.App/environments" : null;
            if (delegation is not null) properties["delegations"] = new JsonArray(new JsonObject {
                ["name"] = "service-delegation", ["properties"] = new JsonObject { ["serviceName"] = delegation }
            });
            if (prefix == "PRIVATEENDPOINTS") properties["privateEndpointNetworkPolicies"] = Enabled("DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES") ? "Disabled" : "Enabled";
            subnets.Add(new JsonObject { ["name"] = Literal(Value($"SUBNET_{prefix}_NAME")), ["properties"] = properties });
        }
        AddDeployment("network", Value("NETWORK_RESOURCE_GROUP"), new JsonArray(Resource("Microsoft.Network/virtualNetworks", "2024-01-01", Value("VNET_NAME"), new JsonObject {
            ["addressSpace"] = new JsonObject { ["addressPrefixes"] = Strings(Value("VNET_ADDRESS_PREFIX")) }, ["subnets"] = subnets
        }, _location)));
    }

    private void AddAsync() {
        if (Integer("REQUESTAPI_MAX_INSTANCE_COUNT") is < 40 or > 1000) {
            throw new InvalidOperationException("RequestAPI maximum instance count must be between 40 and 1000 for Flex Consumption.");
        }
        if (Enabled("PRIVATE_NETWORK_DEPLOYMENT") && Value("REQUESTAPI_LOCATION") != _location) {
            throw new InvalidOperationException("RequestAPI must use the common location when integrated with the selected virtual network.");
        }
        var appGroup = Value("CONTAINER_APP_RESOURCE_GROUP");
        var proxyIdentityId = Id(appGroup, "Microsoft.ManagedIdentity/userAssignedIdentities", Value("CONTAINER_APP_NAME") + "-identity");
        var blobGroup = Value("STORAGE_RESOURCE_GROUP");
        var blobName = Value("STORAGE_ACCOUNT_NAME");
        var blobId = Id(blobGroup, "Microsoft.Storage/storageAccounts", blobName);
        var blobRole = Value("CA_BLOB_ROLE") switch {
            "Storage Blob Data Contributor" => "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
            "Storage Blob Data Owner" => "b7e6dc6d-f1e8-4753-8033-0f276bb0955b",
            "Storage Blob Data Reader" => "2a2b9908-6ea1-4ae2-8e65-a410df84e7d1",
            var role when Guid.TryParse(role, out _) => role,
            _ => throw new InvalidOperationException("For ARM export, enter a built-in Storage Blob Data role name or a role definition GUID in CA_BLOB_ROLE.")
        };
        JsonArray blobs = [Storage(blobName, Value("STORAGE_SKU"), _location), Role(blobId, proxyIdentityId, blobRole, false)];
        if (Enabled("CREATE_CONTAINERS")) {
            foreach (var container in Value("BLOB_CONTAINERS").Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)) {
                blobs.Add(BlobContainer(blobGroup, blobName, container));
            }
        }
        AddDeployment("blob-storage", blobGroup, blobs, "foundation");

        var group = Value("REQUESTAPI_RESOURCE_GROUP");
        var location = Value("REQUESTAPI_LOCATION");
        var name = Value("REQUESTAPI_FUNCTION_APP");
        var storageName = Value("REQUESTAPI_STORAGE_ACCOUNT");
        var storageId = Id(group, "Microsoft.Storage/storageAccounts", storageName);
        var identityId = Id(group, "Microsoft.ManagedIdentity/userAssignedIdentities", name + "-identity");
        var workspaceId = Id(group, "Microsoft.OperationalInsights/workspaces", name + "-logs");
        var insightsId = Id(group, "Microsoft.Insights/components", Value("REQUESTAPI_APPINSIGHTS_NAME"));
        var planId = Id(group, "Microsoft.Web/serverfarms", name + "-plan");
        var functionResources = new JsonArray(
            Storage(storageName, "Standard_LRS", location), BlobContainer(group, storageName, "deployment-package"),
            Resource("Microsoft.ManagedIdentity/userAssignedIdentities", "2023-01-31", name + "-identity", new JsonObject(), location),
            Resource("Microsoft.OperationalInsights/workspaces", "2023-09-01", name + "-logs", new JsonObject {
                ["sku"] = new JsonObject { ["name"] = "PerGB2018" }, ["retentionInDays"] = 30
            }, location));
        var storageRoles = new[] { "b7e6dc6d-f1e8-4753-8033-0f276bb0955b", "974c5e8b-45b9-4653-ba55-5f855dd0fb88", "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3" };
        foreach (var role in storageRoles) functionResources.Add(Role(storageId, identityId, role, true));
        var insights = Resource("Microsoft.Insights/components", "2020-02-02", Value("REQUESTAPI_APPINSIGHTS_NAME"), new JsonObject {
            ["Application_Type"] = "web", ["WorkspaceResourceId"] = $"[{workspaceId}]"
        }, location);
        insights["kind"] = "web";
        insights["dependsOn"] = Strings($"[{workspaceId}]");
        functionResources.Add(insights);
        var plan = Resource("Microsoft.Web/serverfarms", "2024-04-01", name + "-plan", new JsonObject { ["reserved"] = true }, location);
        plan["sku"] = new JsonObject { ["name"] = "FC1", ["tier"] = "FlexConsumption" };
        plan["kind"] = "functionapp";
        functionResources.Add(plan);
        var clientId = $"[reference({identityId}, '2023-01-31').clientId]";
        var serviceBusId = $"resourceId(parameters('serviceBusResourceGroup'), 'Microsoft.ServiceBus/namespaces', {Quote(Value("REQUESTAPI_SERVICEBUS_NAMESPACE"))})";
        var cosmosId = $"resourceId(parameters('cosmosResourceGroup'), 'Microsoft.DocumentDB/databaseAccounts', {Quote(Value("REQUESTAPI_COSMOS_ACCOUNT"))})";
        JsonArray settings = [
            Setting("APPLICATIONINSIGHTS_CONNECTION_STRING", $"[reference({insightsId}, '2020-02-02').ConnectionString]"),
            Setting("AzureWebJobsStorage__accountName", storageName),
            Setting("AzureWebJobsStorage__credential", "managedidentity"), Setting("AzureWebJobsStorage__clientId", clientId),
            Setting("ServiceBusConnection__fullyQualifiedNamespace", $"[replace(replace(reference({serviceBusId}, '2024-01-01').serviceBusEndpoint, 'https://', ''), '/', '')]"),
            Setting("ServiceBusConnection__credential", "managedidentity"), Setting("ServiceBusConnection__clientId", clientId),
            Setting("ServiceBusQueue", Literal(Value("REQUESTAPI_SERVICEBUS_QUEUE"))), Setting("SBFeederQueue", Literal(Value("REQUESTAPI_SERVICEBUS_FEEDER_QUEUE"))),
            Setting("CosmosDbConnection__accountEndpoint", $"[reference({cosmosId}, '2024-05-15').documentEndpoint]"),
            Setting("CosmosDbConnection__credential", "managedidentity"), Setting("CosmosDbConnection__clientId", clientId),
            Setting("CosmosDb__DatabaseName", Literal(Value("REQUESTAPI_COSMOS_DATABASE"))), Setting("CosmosDb__ContainerName", Literal(Value("REQUESTAPI_COSMOS_CONTAINER")))
        ];
        var function = Resource("Microsoft.Web/sites", "2024-04-01", name, new JsonObject {
            ["serverFarmId"] = $"[{planId}]", ["httpsOnly"] = true,
            ["siteConfig"] = new JsonObject { ["minTlsVersion"] = "1.2", ["ftpsState"] = "Disabled", ["appSettings"] = settings },
            ["functionAppConfig"] = new JsonObject {
                ["runtime"] = new JsonObject { ["name"] = Value("REQUESTAPI_RUNTIME_NAME"), ["version"] = Value("REQUESTAPI_RUNTIME_VERSION").Split('.')[0] },
                ["scaleAndConcurrency"] = new JsonObject { ["instanceMemoryMB"] = Integer("REQUESTAPI_INSTANCE_MEMORY_MB"), ["maximumInstanceCount"] = Integer("REQUESTAPI_MAX_INSTANCE_COUNT") },
                ["deployment"] = new JsonObject { ["storage"] = new JsonObject {
                    ["type"] = "blobContainer", ["value"] = $"[concat(reference({storageId}, '2023-05-01').primaryEndpoints.blob, 'deployment-package')]",
                    ["authentication"] = new JsonObject { ["type"] = "UserAssignedIdentity", ["userAssignedIdentityResourceId"] = $"[{identityId}]" }
                } }
            }
        }, location);
        function["kind"] = "functionapp,linux";
        function["identity"] = new JsonObject { ["type"] = "UserAssigned", ["userAssignedIdentities"] = new JsonObject { [$"[{identityId}]"] = new JsonObject() } };
        var dependencies = Strings($"[{planId}]", $"[{insightsId}]", $"[{identityId}]", $"[{Id(group, "Microsoft.Storage/storageAccounts/blobServices/containers", storageName, "default", "deployment-package")}]" );
        foreach (var role in storageRoles) dependencies.Add(JsonValue.Create($"[extensionResourceId({storageId}, 'Microsoft.Authorization/roleAssignments', guid({storageId}, {identityId}, {Quote(role)}))]"));
        function["dependsOn"] = dependencies;
        if (Enabled("PRIVATE_NETWORK_DEPLOYMENT")) function["properties"]!["virtualNetworkSubnetId"] = $"[{Id(Value("NETWORK_RESOURCE_GROUP"), "Microsoft.Network/virtualNetworks/subnets", Value("VNET_NAME"), Value("SUBNET_AZUREFUNCTIONS_NAME"))}]";
        functionResources.Add(function);
        AddDeployment("request-api", group, functionResources, "foundation");

        var busRoles = new JsonArray();
        foreach (var role in new[] { "4f6d3b9b-027b-4f4c-9142-0e9d2f14d0af", "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39" }) {
            var assignment = Role(serviceBusId, identityId, role, false);
            assignment.Remove("dependsOn");
            busRoles.Add(assignment);
        }
        var proxySender = Role(serviceBusId, proxyIdentityId, "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39", false);
        proxySender.Remove("dependsOn");
        busRoles.Add(proxySender);
        AddDeployment("service-bus-access", "[parameters('serviceBusResourceGroup')]", busRoles, "request-api");
        var cosmosRole = Resource("Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments", "2024-05-15", "cosmos-access", new JsonObject {
            ["roleDefinitionId"] = $"[concat({cosmosId}, '/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002')]",
            ["principalId"] = $"[reference({identityId}, '2023-01-31').principalId]", ["scope"] = $"[{cosmosId}]"
        });
        cosmosRole["name"] = $"[concat({Quote(Value("REQUESTAPI_COSMOS_ACCOUNT"))}, '/', guid({cosmosId}, {identityId}, 'data-contributor'))]";
        AddDeployment("cosmos-access", "[parameters('cosmosResourceGroup')]", new JsonArray(cosmosRole), "request-api");
    }

    private static JsonObject Storage(string name, string sku, string location) {
        var resource = Resource("Microsoft.Storage/storageAccounts", "2023-05-01", name, new JsonObject {
            ["minimumTlsVersion"] = "TLS1_2", ["supportsHttpsTrafficOnly"] = true,
            ["allowBlobPublicAccess"] = false, ["allowSharedKeyAccess"] = false, ["publicNetworkAccess"] = "Enabled"
        }, location);
        resource["kind"] = "StorageV2";
        resource["sku"] = new JsonObject { ["name"] = sku };
        return resource;
    }

    private static JsonObject BlobContainer(string group, string account, string name) {
        var resource = Resource("Microsoft.Storage/storageAccounts/blobServices/containers", "2023-05-01", account + "/default/" + name, new JsonObject { ["publicAccess"] = "None" });
        resource["dependsOn"] = Strings($"[{Id(group, "Microsoft.Storage/storageAccounts", account)}]");
        return resource;
    }

    private JsonObject ExternalGroupParameters() => new() {
        ["serviceBusResourceGroup"] = new JsonObject {
            ["type"] = "string", ["defaultValue"] = Value("REQUESTAPI_RESOURCE_GROUP"),
            ["metadata"] = new JsonObject { ["description"] = "Resource group of the EXISTING Service Bus namespace. Defaults to the RequestAPI resource group; change if different." }
        },
        ["cosmosResourceGroup"] = new JsonObject {
            ["type"] = "string", ["defaultValue"] = Value("REQUESTAPI_RESOURCE_GROUP"),
            ["metadata"] = new JsonObject { ["description"] = "Resource group of the EXISTING Cosmos DB account. Defaults to the RequestAPI resource group; change if different." }
        }
    };

    private void AddDns() {
        var group = Value("NETWORK_RESOURCE_GROUP");
        var vnetId = Id(group, "Microsoft.Network/virtualNetworks", Value("VNET_NAME"));
        var environmentId = Id(Value("CONTAINER_APP_RESOURCE_GROUP"), "Microsoft.App/managedEnvironments", Value("ENVIRONMENT_NAME"));
        var appId = Id(Value("CONTAINER_APP_RESOURCE_GROUP"), "Microsoft.App/containerApps", Value("CONTAINER_APP_NAME"));
        var zone = Value("DNS_ZONE_NAME");
        var zoneId = Id(group, "Microsoft.Network/privateDnsZones", zone);
        var zoneResources = new JsonArray(Resource("Microsoft.Network/privateDnsZones", "2020-06-01", zone, new JsonObject(), "global"));
        var link = Resource("Microsoft.Network/privateDnsZones/virtualNetworkLinks", "2020-06-01", zone + "/" + Value("VNET_NAME") + "-link", new JsonObject {
            ["registrationEnabled"] = false, ["virtualNetwork"] = new JsonObject { ["id"] = $"[{vnetId}]" }
        }, "global");
        link["dependsOn"] = Strings($"[{zoneId}]");
        zoneResources.Add(link);
        var cname = Resource("Microsoft.Network/privateDnsZones/CNAME", "2020-06-01", zone + "/" + Value("ACA_RECORD_NAME"), new JsonObject {
            ["ttl"] = 300, ["cnameRecord"] = new JsonObject {
                ["cname"] = string.IsNullOrWhiteSpace(Value("ACA_INTERNAL_FQDN"))
                    ? $"[reference({appId}, '2024-03-01').configuration.ingress.fqdn]" : Literal(Value("ACA_INTERNAL_FQDN"))
            }
        });
        cname["dependsOn"] = Strings($"[{zoneId}]");
        zoneResources.Add(cname);
        if (!string.IsNullOrWhiteSpace(Value("APIM_PRIVATE_IP"))) {
            var apim = Resource("Microsoft.Network/privateDnsZones/A", "2020-06-01", zone + "/" + Value("APIM_RECORD_NAME"), new JsonObject {
                ["ttl"] = 300, ["aRecords"] = new JsonArray(new JsonObject { ["ipv4Address"] = Value("APIM_PRIVATE_IP") })
            });
            apim["dependsOn"] = Strings($"[{zoneId}]");
            zoneResources.Add(apim);
        }
        var domain = "parameters('environmentDomain')";
        var domainId = $"resourceId('Microsoft.Network/privateDnsZones', {domain})";
        var environmentZone = Resource("Microsoft.Network/privateDnsZones", "2020-06-01", "environment-zone", new JsonObject(), "global");
        environmentZone["name"] = $"[{domain}]";
        var environmentLink = Resource("Microsoft.Network/privateDnsZones/virtualNetworkLinks", "2020-06-01", "environment-link", new JsonObject {
            ["registrationEnabled"] = false, ["virtualNetwork"] = new JsonObject { ["id"] = $"[{vnetId}]" }
        }, "global");
        environmentLink["name"] = $"[concat({domain}, '/environment-link')]";
        environmentLink["dependsOn"] = Strings($"[{domainId}]");
        var wildcard = Resource("Microsoft.Network/privateDnsZones/A", "2020-06-01", "environment-wildcard", new JsonObject {
            ["ttl"] = 300, ["aRecords"] = new JsonArray(new JsonObject { ["ipv4Address"] = $"[reference({environmentId}, '2024-03-01').staticIp]" })
        });
        wildcard["name"] = $"[concat({domain}, '/*')]";
        wildcard["dependsOn"] = Strings($"[{domainId}]");
        zoneResources.Add(environmentZone);
        zoneResources.Add(environmentLink);
        zoneResources.Add(wildcard);
        var deployment = AddDeployment("private-dns", group, zoneResources, "network", "container-app");
        var parameters = deployment["properties"]!["template"]!["parameters"] as JsonObject ?? new JsonObject();
        if (parameters.Parent is null) deployment["properties"]!["template"]!["parameters"] = parameters;
        parameters["environmentDomain"] = new JsonObject { ["type"] = "string" };
        var arguments = deployment["properties"]!["parameters"] as JsonObject ?? new JsonObject();
        if (arguments.Parent is null) deployment["properties"]!["parameters"] = arguments;
        arguments["environmentDomain"] = new JsonObject { ["value"] = $"[reference({environmentId}, '2024-03-01').defaultDomain]" };
    }

    private JsonObject AddDeployment(string name, string group, JsonArray resources, params string[] dependencies) {
        var deploymentName = Value("CONTAINER_APP_NAME") + "-" + name;
        var groupExpression = group.StartsWith("[parameters(", StringComparison.Ordinal) ? group[1..^1] : Quote(group);
        _deploymentIds[name] = $"[resourceId(subscription().subscriptionId, {groupExpression}, 'Microsoft.Resources/deployments', {Quote(deploymentName)})]";
        var deployment = new JsonObject {
            ["type"] = "Microsoft.Resources/deployments", ["apiVersion"] = "2022-09-01", ["name"] = deploymentName,
            ["resourceGroup"] = group, ["dependsOn"] = Strings(dependencies.Select(dependency => _deploymentIds[dependency]).ToArray()),
            ["properties"] = new JsonObject {
                ["mode"] = "Incremental", ["expressionEvaluationOptions"] = new JsonObject { ["scope"] = "inner" },
                ["template"] = Template(resources)
            }
        };
        if (_stage != "application" && !group.StartsWith("[parameters(", StringComparison.Ordinal)) {
            deployment["dependsOn"]!.AsArray().Insert(0, JsonValue.Create($"[subscriptionResourceId('Microsoft.Resources/resourceGroups', {Quote(group)})]"));
        }
        if (_stage != "bootstrap" && Enabled("ASYNC_DEPLOYMENT")) {
            deployment["properties"]!["template"]!["parameters"] = ExternalGroupParameters();
            deployment["properties"]!["parameters"] = new JsonObject {
                ["serviceBusResourceGroup"] = new JsonObject { ["value"] = "[parameters('serviceBusResourceGroup')]" },
                ["cosmosResourceGroup"] = new JsonObject { ["value"] = "[parameters('cosmosResourceGroup')]" }
            };
        }
        _deployments.Add(deployment);
        return deployment;
    }

    private static JsonObject Role(string scopeId, string identityId, string roleId, bool localIdentity) {
        var dependencies = Strings($"[{scopeId}]");
        if (localIdentity) dependencies.Add(JsonValue.Create($"[{identityId}]"));
        return new JsonObject {
            ["type"] = "Microsoft.Authorization/roleAssignments", ["apiVersion"] = "2022-04-01",
            ["name"] = $"[guid({scopeId}, {identityId}, {Quote(roleId)})]", ["scope"] = $"[{scopeId}]",
            ["dependsOn"] = dependencies,
            ["properties"] = new JsonObject {
                ["roleDefinitionId"] = $"[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', {Quote(roleId)})]",
                ["principalId"] = $"[reference({identityId}, '2023-01-31').principalId]", ["principalType"] = "ServicePrincipal"
            }
        };
    }

    private static JsonObject Template(JsonArray resources) => new() {
        ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
        ["contentVersion"] = "1.0.0.0", ["resources"] = resources
    };

    private static JsonObject Resource(string type, string version, string name, JsonObject properties, string? location = null) {
        var resource = new JsonObject { ["type"] = type, ["apiVersion"] = version, ["name"] = Literal(name), ["properties"] = properties };
        if (location is not null) resource["location"] = Literal(location);
        return resource;
    }

    private static JsonObject Setting(string name, string value) => new() { ["name"] = name, ["value"] = value };

    /// <summary>Creates an explicit, locally executed image-publishing step without deploying infrastructure.</summary>
    public static string GeneratePublishScript(IReadOnlyDictionary<string, string> values, string proxyVersion, string healthVersion) {
        var proxyTag = string.IsNullOrWhiteSpace(values["PROXY_VERSION_OVERRIDE"]) ? proxyVersion : values["PROXY_VERSION_OVERRIDE"];
        var healthTag = string.IsNullOrWhiteSpace(values["HEALTHPROBE_VERSION_OVERRIDE"]) ? healthVersion : values["HEALTHPROBE_VERSION_OVERRIDE"];
        if (!proxyTag.StartsWith('v')) proxyTag = "v" + proxyTag;
        if (!healthTag.StartsWith('v')) healthTag = "v" + healthTag;
        var output = new StringBuilder("#!/usr/bin/env bash\nset -euo pipefail\n");
        output.AppendLine("subscription=\"${1:?Usage: bash 02-publish-images.sh SUBSCRIPTION_ID (run from the repository root)}\"");
        output.AppendLine("command -v az >/dev/null || { printf '%s\\n' 'Azure CLI is required; authenticate before running this script.' >&2; exit 1; }");
        output.AppendLine("[[ -d src/SimpleL7Proxy && -d src/HealthProbe ]] || { printf '%s\\n' 'Run from the SimpleL7Proxy repository root.' >&2; exit 1; }");
        output.Append("registry=").AppendLine(ShellLiteral(values["ACR_NAME"]));
        output.Append("resource_group=").AppendLine(ShellLiteral(values["CONTAINER_APP_RESOURCE_GROUP"]));
        output.AppendLine("az account show --subscription \"$subscription\" --query '{subscription:id,tenant:tenantId}' --output table");
        output.AppendLine("login_server=$(az acr show --subscription \"$subscription\" --name \"$registry\" --resource-group \"$resource_group\" --query loginServer --output tsv)");
        output.AppendLine("[[ -n \"$login_server\" ]] || { printf '%s\\n' 'Registry login server could not be resolved.' >&2; exit 1; }");
        if (values["BUILD_METHOD"] == "local") {
            output.AppendLine("command -v docker >/dev/null || { printf '%s\\n' 'Docker is required for local builds.' >&2; exit 1; }");
            output.AppendLine("docker info >/dev/null");
            output.AppendLine("az acr login --subscription \"$subscription\" --name \"$registry\"");
        } else if (values["BUILD_METHOD"] != "remote") {
            throw new ArgumentException("Build method must be remote or local.", nameof(values));
        }
        var images = new List<(string Name, string Tag, string Dockerfile)> {
            (values["PROXY_IMAGE_NAME"], proxyTag, values["DOCKERFILE_PATH"])
        };
        if (values["HEALTHPROBE_TYPE"] == "sidecar") images.Add((values["HEALTH_IMAGE_NAME"], healthTag, "HealthProbe/Dockerfile"));
        foreach (var image in images) {
            output.Append("image=").AppendLine(ShellLiteral(image.Name + ":" + image.Tag));
            output.Append("dockerfile=").AppendLine(ShellLiteral(image.Dockerfile));
            output.AppendLine("[[ -f \"src/$dockerfile\" ]] || { printf '%s\\n' \"Dockerfile not found: src/$dockerfile\" >&2; exit 1; }");
            if (values["BUILD_METHOD"] == "local") {
                output.AppendLine("docker build --tag \"$login_server/$image\" --file \"src/$dockerfile\" src");
                output.AppendLine("docker push \"$login_server/$image\"");
            } else {
                output.AppendLine("az acr build --subscription \"$subscription\" --registry \"$registry\" --image \"$image\" --file \"src/$dockerfile\" src");
            }
            output.AppendLine("az acr repository show --subscription \"$subscription\" --name \"$registry\" --image \"$image\" --query digest --output tsv");
        }
        output.AppendLine("printf '%s\\n' 'Image tags verified. Deploy 03-application.json in the same subscription.'");
        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string ShellLiteral(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static JsonArray Strings(params string[] values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    private static string Literal(string value) => value.StartsWith('[') ? "[" + value : value;
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Id(string group, string type, params string[] names) => $"resourceId({Quote(group)}, {Quote(type)}, {string.Join(", ", names.Select(Quote))})";
    private string Value(string key) => _values[key];
    private bool Enabled(string key) => Value(key) is "true" or "yes";
    private int Integer(string key) => int.Parse(Value(key), CultureInfo.InvariantCulture);
    private decimal Number(string key) => decimal.Parse(Value(key), CultureInfo.InvariantCulture);
}