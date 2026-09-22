targetScope = 'subscription'

import { DeploymentSettings } from './types.bicep'

@description('Deployment Setup values consumed by the static Bicep templates.')
param settings DeploymentSettings

resource existingEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = if (settings.USE_EXISTING_ENVIRONMENT) {
  name: settings.ENVIRONMENT_NAME
  scope: resourceGroup(settings.ENVIRONMENT_RESOURCE_GROUP)
}

module network 'modules/network.bicep' = if (settings.PRIVATE_NETWORK_DEPLOYMENT) {
  scope: resourceGroup(settings.NETWORK_RESOURCE_GROUP)
  params: {
    settings: settings
  }
}

module foundation 'modules/foundation.bicep' = {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    acaSubnetId: network.?outputs.acaSubnetId ?? ''
    existingEnvironmentId: existingEnvironment.?id ?? ''
    existingEnvironmentDomain: existingEnvironment.?properties.defaultDomain ?? ''
    existingEnvironmentStaticIp: existingEnvironment.?properties.staticIp ?? ''
  }
}

module configuration 'modules/configuration.bicep' = {
  scope: resourceGroup(settings.APPCONFIG_RESOURCE_GROUP)
  params: {
    settings: settings
  }
}

module companionAppBootstrap 'modules/companion-app.bicep' = if (settings.DEPLOY_COMPANION_APP) {
  scope: resourceGroup(settings.COMPANION_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: false
    environmentId: foundation.outputs.environmentId
    appConfigurationEndpoint: ''
  }
}

module metricsServerBootstrap 'modules/metrics-server.bicep' = if (settings.DEPLOY_METRICS_SERVER) {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: false
    environmentId: foundation.outputs.environmentId
  }
}

module blobStorage 'modules/blob-storage.bicep' = if (settings.ASYNC_DEPLOYMENT) {
  scope: resourceGroup(settings.STORAGE_RESOURCE_GROUP)
  params: {
    settings: settings
  }
}

module requestApi 'modules/request-api.bicep' = if (settings.ASYNC_DEPLOYMENT) {
  scope: resourceGroup(settings.REQUESTAPI_RESOURCE_GROUP)
  params: {
    settings: settings
    functionsSubnetId: network.?outputs.functionsSubnetId ?? ''
  }
  dependsOn: [
    foundation
  ]
}

module cosmosAccess 'modules/cosmos-access.bicep' = if (settings.ASYNC_DEPLOYMENT) {
  scope: resourceGroup(settings.COSMOS_RESOURCE_GROUP)
  params: {
    settings: settings
    requestIdentityId: requestApi.?outputs.identityId ?? ''
    requestIdentityPrincipalId: requestApi.?outputs.identityPrincipalId ?? ''
  }
}

module containerAppBootstrap 'modules/container-app.bicep' = {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: false
    environmentId: foundation.outputs.environmentId
    appInsightsConnectionString: foundation.outputs.appInsightsConnectionString
    appConfigurationEndpoint: ''
    blobEndpoint: blobStorage.?outputs.blobEndpoint ?? ''
    requestApiHostName: requestApi.?outputs.functionHostName ?? ''
  }
}

module registryAccess 'modules/registry-access.bicep' = {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    containerAppId: containerAppBootstrap.outputs.containerAppId
    containerAppPrincipalId: containerAppBootstrap.outputs.identityPrincipalId
    companionAppId: companionAppBootstrap.?outputs.appId ?? ''
    companionAppPrincipalId: companionAppBootstrap.?outputs.identityPrincipalId ?? ''
    metricsServerId: metricsServerBootstrap.?outputs.appId ?? ''
    metricsServerPrincipalId: metricsServerBootstrap.?outputs.identityPrincipalId ?? ''
  }
}

module configurationAccess 'modules/configuration-access.bicep' = {
  scope: resourceGroup(settings.APPCONFIG_RESOURCE_GROUP)
  params: {
    settings: settings
    configurationStoreId: configuration.outputs.configurationStoreId
    containerAppId: containerAppBootstrap.outputs.containerAppId
    containerAppPrincipalId: containerAppBootstrap.outputs.identityPrincipalId
    companionAppId: companionAppBootstrap.?outputs.appId ?? ''
    companionAppPrincipalId: companionAppBootstrap.?outputs.identityPrincipalId ?? ''
  }
}

module companionApp 'modules/companion-app.bicep' = if (settings.DEPLOY_COMPANION_APP) {
  scope: resourceGroup(settings.COMPANION_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: true
    environmentId: foundation.outputs.environmentId
    appConfigurationEndpoint: configuration.outputs.endpoint
  }
  dependsOn: [
    registryAccess
    configurationAccess
  ]
}

module metricsServer 'modules/metrics-server.bicep' = if (settings.DEPLOY_METRICS_SERVER) {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: true
    environmentId: foundation.outputs.environmentId
  }
  dependsOn: [
    registryAccess
  ]
}

module blobAccess 'modules/blob-access.bicep' = if (settings.ASYNC_DEPLOYMENT) {
  scope: resourceGroup(settings.STORAGE_RESOURCE_GROUP)
  params: {
    settings: settings
    containerAppId: containerAppBootstrap.outputs.containerAppId
    containerAppPrincipalId: containerAppBootstrap.outputs.identityPrincipalId
  }
}

module serviceBusAccess 'modules/service-bus-access.bicep' = if (settings.ASYNC_DEPLOYMENT) {
  scope: resourceGroup(settings.SERVICEBUS_RESOURCE_GROUP)
  params: {
    settings: settings
    requestIdentityId: requestApi.?outputs.identityId ?? ''
    requestIdentityPrincipalId: requestApi.?outputs.identityPrincipalId ?? ''
    containerAppId: containerAppBootstrap.outputs.containerAppId
    containerAppPrincipalId: containerAppBootstrap.outputs.identityPrincipalId
  }
}

module containerApp 'modules/container-app.bicep' = {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    settings: settings
    usePrivateRegistry: true
    environmentId: foundation.outputs.environmentId
    appInsightsConnectionString: foundation.outputs.appInsightsConnectionString
    appConfigurationEndpoint: configuration.outputs.endpoint
    blobEndpoint: blobStorage.?outputs.blobEndpoint ?? ''
    requestApiHostName: requestApi.?outputs.functionHostName ?? ''
  }
  dependsOn: [
    registryAccess
    configurationAccess
    blobAccess
    serviceBusAccess
  ]
}

module privateDns 'modules/private-dns.bicep' = if (settings.PRIVATE_NETWORK_DEPLOYMENT) {
  scope: resourceGroup(settings.NETWORK_RESOURCE_GROUP)
  params: {
    settings: settings
    environmentDomain: foundation.outputs.environmentDomain
    environmentStaticIp: foundation.outputs.environmentStaticIp
    proxyFqdn: containerApp.outputs.proxyFqdn
  }
}

output proxyUrl string = 'https://${containerApp.outputs.proxyFqdn}'
output companionAppUrl string = companionApp.?outputs.url ?? ''
