targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param configurationStoreId string
param containerAppId string
param containerAppPrincipalId string
param companionAppId string
param companionAppPrincipalId string

var appConfigurationDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071'
var appConfigurationDataOwnerRoleId = '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'

resource configurationStore 'Microsoft.AppConfiguration/configurationStores@2024-05-01' existing = {
  name: settings.APPCONFIG_NAME
}

resource dataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(configurationStoreId, containerAppId, appConfigurationDataReaderRoleId)
  scope: configurationStore
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataReaderRoleId)
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource companionDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (settings.DEPLOY_COMPANION_APP) {
  name: guid(configurationStoreId, companionAppId, appConfigurationDataOwnerRoleId)
  scope: configurationStore
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataOwnerRoleId)
    principalId: companionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
