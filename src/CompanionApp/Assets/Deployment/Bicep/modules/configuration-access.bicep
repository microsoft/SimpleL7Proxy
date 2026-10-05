targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param configurationStoreId string
param companionAppId string
param companionAppPrincipalId string
param metricsServerId string
param metricsServerPrincipalId string
var appConfigurationDataOwnerRoleId = '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'
var appConfigurationDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071'

resource configurationStore 'Microsoft.AppConfiguration/configurationStores@2024-05-01' existing = {
  name: settings.APPCONFIG_NAME
}

resource companionDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(configurationStoreId, companionAppId, appConfigurationDataOwnerRoleId)
  scope: configurationStore
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataOwnerRoleId)
    principalId: companionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource metricsDataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (settings.DEPLOY_METRICS_SERVER) {
  name: guid(configurationStoreId, metricsServerId, appConfigurationDataReaderRoleId)
  scope: configurationStore
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataReaderRoleId)
    principalId: metricsServerPrincipalId
    principalType: 'ServicePrincipal'
  }
}
