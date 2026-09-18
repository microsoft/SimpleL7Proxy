targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param containerAppId string
param containerAppPrincipalId string

var blobRoleIds = {
  'Storage Blob Data Contributor': 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  'Storage Blob Data Owner': 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  'Storage Blob Data Reader': '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
}
var blobRoleId = blobRoleIds[?settings.CA_BLOB_ROLE] ?? settings.CA_BLOB_ROLE

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: settings.STORAGE_ACCOUNT_NAME
}

resource proxyBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, containerAppId, blobRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobRoleId)
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
