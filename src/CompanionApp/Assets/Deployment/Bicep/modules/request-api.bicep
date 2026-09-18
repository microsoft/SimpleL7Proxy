targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param functionsSubnetId string

var storageRoleIds = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
]
var serviceBusId = resourceId(settings.SERVICEBUS_RESOURCE_GROUP, 'Microsoft.ServiceBus/namespaces', settings.REQUESTAPI_SERVICEBUS_NAMESPACE)
var cosmosId = resourceId(settings.COSMOS_RESOURCE_GROUP, 'Microsoft.DocumentDB/databaseAccounts', settings.REQUESTAPI_COSMOS_ACCOUNT)

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: settings.REQUESTAPI_STORAGE_ACCOUNT
  location: settings.REQUESTAPI_LOCATION
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  name: 'default'
  parent: storageAccount
}

resource deploymentPackage 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: 'deployment-package'
  parent: blobService
  properties: {
    publicAccess: 'None'
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${settings.REQUESTAPI_FUNCTION_APP}-identity'
  location: settings.REQUESTAPI_LOCATION
}

resource storageRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in storageRoleIds: {
  name: guid(storageAccount.id, identity.id, roleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}]

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${settings.REQUESTAPI_FUNCTION_APP}-logs'
  location: settings.REQUESTAPI_LOCATION
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: settings.REQUESTAPI_APPINSIGHTS_NAME
  location: settings.REQUESTAPI_LOCATION
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${settings.REQUESTAPI_FUNCTION_APP}-plan'
  location: settings.REQUESTAPI_LOCATION
  kind: 'functionapp'
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: settings.REQUESTAPI_FUNCTION_APP
  location: settings.REQUESTAPI_LOCATION
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: union(
    {
      serverFarmId: plan.id
      httpsOnly: true
      siteConfig: {
        minTlsVersion: '1.2'
        ftpsState: 'Disabled'
        appSettings: [
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: insights.properties.ConnectionString
          }
          {
            name: 'AzureWebJobsStorage__accountName'
            value: storageAccount.name
          }
          {
            name: 'AzureWebJobsStorage__credential'
            value: 'managedidentity'
          }
          {
            name: 'AzureWebJobsStorage__clientId'
            value: identity.properties.clientId
          }
          {
            name: 'ServiceBusConnection__fullyQualifiedNamespace'
            value: replace(replace(reference(serviceBusId, '2024-01-01').serviceBusEndpoint, 'https://', ''), '/', '')
          }
          {
            name: 'ServiceBusConnection__credential'
            value: 'managedidentity'
          }
          {
            name: 'ServiceBusConnection__clientId'
            value: identity.properties.clientId
          }
          {
            name: 'ServiceBusQueue'
            value: settings.REQUESTAPI_SERVICEBUS_QUEUE
          }
          {
            name: 'SBFeederQueue'
            value: settings.REQUESTAPI_SERVICEBUS_FEEDER_QUEUE
          }
          {
            name: 'CosmosDbConnection__accountEndpoint'
            value: reference(cosmosId, '2024-05-15').documentEndpoint
          }
          {
            name: 'CosmosDbConnection__credential'
            value: 'managedidentity'
          }
          {
            name: 'CosmosDbConnection__clientId'
            value: identity.properties.clientId
          }
          {
            name: 'CosmosDb__DatabaseName'
            value: settings.REQUESTAPI_COSMOS_DATABASE
          }
          {
            name: 'CosmosDb__ContainerName'
            value: settings.REQUESTAPI_COSMOS_CONTAINER
          }
        ]
      }
      functionAppConfig: {
        runtime: {
          name: settings.REQUESTAPI_RUNTIME_NAME
          version: split(settings.REQUESTAPI_RUNTIME_VERSION, '.')[0]
        }
        scaleAndConcurrency: {
          instanceMemoryMB: settings.REQUESTAPI_INSTANCE_MEMORY_MB
          maximumInstanceCount: settings.REQUESTAPI_MAX_INSTANCE_COUNT
        }
        deployment: {
          storage: {
            type: 'blobContainer'
            value: '${storageAccount.properties.primaryEndpoints.blob}deployment-package'
            authentication: {
              type: 'UserAssignedIdentity'
              userAssignedIdentityResourceId: identity.id
            }
          }
        }
      }
    },
    settings.PRIVATE_NETWORK_DEPLOYMENT ? {
      virtualNetworkSubnetId: functionsSubnetId
    } : {}
  )
  dependsOn: [
    deploymentPackage
    storageRoles
  ]
}

output identityId string = identity.id
output identityPrincipalId string = identity.properties.principalId
output functionHostName string = functionApp.properties.defaultHostName
