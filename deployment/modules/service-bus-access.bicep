targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param requestIdentityId string
param requestIdentityPrincipalId string
param containerAppId string
param containerAppPrincipalId string

var requestRoleIds = [
  '4f6d3b9b-027b-4f4c-9142-0e9d2f14d0af'
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
]
var senderRoleId = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: settings.REQUESTAPI_SERVICEBUS_NAMESPACE
}

resource requestAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in requestRoleIds: {
  name: guid(serviceBus.id, requestIdentityId, roleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    principalId: requestIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}]

resource proxySender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBus.id, containerAppId, senderRoleId)
  scope: serviceBus
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', senderRoleId)
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
