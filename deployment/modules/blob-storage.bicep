targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: settings.STORAGE_ACCOUNT_NAME
  location: settings.LOCATION
  kind: 'StorageV2'
  sku: {
    name: settings.STORAGE_SKU
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: (settings.?DEPLOY_STORAGE_NETWORK ?? false) ? 'Disabled' : 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  name: 'default'
  parent: storageAccount
}

var containerNames = union(settings.CREATE_CONTAINERS ? settings.BLOB_CONTAINERS : [], (settings.?DEPLOY_STORAGE_NETWORK ?? false) ? ['profiles'] : [])

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for containerName in containerNames: {
  name: containerName
  parent: blobService
  properties: {
    publicAccess: 'None'
  }
}]

output storageAccountId string = storageAccount.id
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
