targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param containerAppId string
param containerAppPrincipalId string

var appConfigurationDataReaderRoleId = '516239f1-63e1-4d78-a4de-a74fb236a071'

resource configurationStore 'Microsoft.AppConfiguration/configurationStores@2024-05-01' existing = {
  name: settings.APPCONFIG_NAME
}

resource dataReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(configurationStore.id, containerAppId, appConfigurationDataReaderRoleId)
  scope: configurationStore
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataReaderRoleId)
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
