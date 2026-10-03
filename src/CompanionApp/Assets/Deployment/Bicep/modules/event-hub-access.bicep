targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param companionAppPrincipalId string

var eventHubsDataReceiverRoleId = 'a638d3c7-ab3a-418d-83e6-5f17a39d4fde'

resource eventHubNamespace 'Microsoft.EventHub/namespaces@2024-01-01' existing = {
  name: settings.COMPANION_EVENTHUB_NAMESPACE
}

resource eventHub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' existing = {
  parent: eventHubNamespace
  name: settings.COMPANION_EVENTHUB_NAME
}

resource companionDataReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventHub.id, companionAppPrincipalId, eventHubsDataReceiverRoleId)
  scope: eventHub
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', eventHubsDataReceiverRoleId)
    principalId: companionAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
