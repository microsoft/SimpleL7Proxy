@description('Specifies the name of the deployment environment.')
param environmentName string = 'dev'

@description('Specifies the Azure location where resources should be created.')
param location string = resourceGroup().location

@description('The name of the Container App to create for SimpleL7Proxy.')
param containerAppName string = 's7p-${environmentName}'

@description('The name of the Container App Environment to create.')
param containerAppEnvironmentName string = 'cae-${environmentName}-s7p'

@description('The name of the Container Registry to use.')
param containerRegistryName string = 'acr${environmentName}s7p'

@description('The minimum number of Container App replicas.')
param containerAppMinReplicas int = 1

@description('The maximum number of Container App replicas.')
param containerAppMaxReplicas int = 5

@description('Flag to determine if VNET should be used for Container App Environment.')
param useVNET bool = false

@description('VNET address space if using VNET.')
param vnetAddressPrefix string = '10.0.0.0/16'

@description('Subnet address space if using VNET.')
param subnetAddressPrefix string = '10.0.0.0/21'

@description('Flag to enable authentication for the Container App.')
param enableAuthentication bool = false

@description('Authentication type (entraID, servicebus, none).')
@allowed([
  'entraID'
  'servicebus'
  'none'
])
param authType string = 'none'

@description('The name of the Storage Account to create.')
param storageAccountName string = 'st${environmentName}s7p${uniqueString(resourceGroup().id)}'

@description('Flag to enable async storage features.')
param useAsyncStorage bool = false

@description('Comma-separated list of backend URLs.')
param backendHostURLs string

@description('Default timeout for requests in milliseconds.')
param defaultRequestTimeout int = 100000

@description('Flag to enable Application Insights.')
param enableAppInsights bool = true

@description('The name of the Log Analytics workspace.')
param logAnalyticsWorkspaceName string = 'log-${environmentName}-s7p'

@description('Number of priority levels to configure.')
param queuePriorityLevels int = 3

@description('Enable auto-scaling based on HTTP concurrent requests.')
param enableScaleRule bool = true

@description('Number of concurrent requests that trigger scaling.')
param httpConcurrentRequestsThreshold int = 10

var backendHostsList = split(backendHostURLs, ',')

// Container Registry
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: containerRegistryName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

// Log Analytics workspace
resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// Application Insights
resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = if (enableAppInsights) {
  name: 'ai-${containerAppName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

// Storage Account
resource storageAccount 'Microsoft.Storage/storageAccounts@2022-09-01' = {
  name: storageAccountName
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

// Blob Service
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2022-09-01' = {
  name: 'default'
  parent: storageAccount
}

// Create container for async storage if enabled
resource asyncStorageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2022-09-01' = if (useAsyncStorage) {
  name: 'async-storage'
  parent: blobService
}

// Virtual Network if useVNET is true
resource vnet 'Microsoft.Network/virtualNetworks@2023-05-01' = if (useVNET) {
  name: 'vnet-${environmentName}-s7p'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetAddressPrefix
      ]
    }
    subnets: [
      {
        name: 'subnet-container-apps'
        properties: {
          addressPrefix: subnetAddressPrefix
          delegations: [
            {
              name: 'Microsoft.App/containerAppsEnvironments'
              properties: {
                serviceName: 'Microsoft.App/containerAppsEnvironments'
              }
            }
          ]
        }
      }
    ]
  }
}

// Container App Environment
resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: containerAppEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: useVNET ? {
      infrastructureSubnetId: vnet.properties.subnets[0].id
      internal: false
    } : null
  }
}

// Container App
resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: containerAppName
  location: location
  properties: {
    managedEnvironmentId: containerAppEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: '${containerRegistry.name}.azurecr.io'
          username: containerRegistry.listCredentials().username
          passwordSecretRef: 'registry-password'
        }
      ]
      secrets: [
        {
          name: 'registry-password'
          value: containerRegistry.listCredentials().passwords[0].value
        }
        {
          name: 'storage-connection-string'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${listKeys(storageAccount.id, storageAccount.apiVersion).keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'simple-l7-proxy'
          image: '${containerRegistry.name}.azurecr.io/simple-l7-proxy:latest'
          env: [
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: environmentName == 'prod' ? 'Production' : 'Development'
            }
            {
              name: 'S7P_BackendHosts__0'
              value: backendHostsList[0]
            }
            {
              name: 'S7P_DefaultTimeout'
              value: string(defaultRequestTimeout)
            }
            {
              name: 'S7P_PriorityLevels'
              value: string(queuePriorityLevels)
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: enableAppInsights ? applicationInsights.properties.ConnectionString : ''
            }
            {
              name: 'S7P_StorageConnectionString'
              secretRef: 'storage-connection-string'
            }
            {
              name: 'S7P_AsyncStorage'
              value: string(useAsyncStorage)
            }
          ]
          resources: {
            cpu: '0.5'
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: containerAppMinReplicas
        maxReplicas: containerAppMaxReplicas
        rules: enableScaleRule ? [
          {
            name: 'http-scaling-rule'
            http: {
              metadata: {
                concurrentRequests: string(httpConcurrentRequestsThreshold)
              }
            }
          }
        ] : []
      }
    }
  }
}

// Add additional backend hosts as environment variables
@batchSize(1)
resource updateContainerAppSettings 'Microsoft.App/containerApps/configurations@2023-05-01' = [for (host, i) in backendHostsList: if (i > 0) {
  name: 'settings'
  parent: containerApp
  properties: {
    secrets: []
    activeRevisionsMode: 'single'
    template: {
      containers: [
        {
          name: 'simple-l7-proxy'
          env: [
            {
              name: 'S7P_BackendHosts__${i}'
              value: host
            }
          ]
        }
      ]
    }
  }
}]

// Output values
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output containerRegistryLoginServer string = containerRegistry.properties.loginServer
output storageAccountName string = storageAccount.name
