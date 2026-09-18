targetScope = 'subscription'

import { DeploymentSettings } from './types.bicep'

@description('Deployment Setup values shared by the bootstrap and application stages.')
param settings DeploymentSettings

resource resourceGroups 'Microsoft.Resources/resourceGroups@2022-09-01' = [for groupName in settings.RESOURCE_GROUPS: {
  name: groupName
  location: settings.LOCATION
}]

module registry 'modules/registry.bicep' = {
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
  params: {
    location: settings.LOCATION
    registryName: settings.ACR_NAME
    registrySku: settings.ACR_SKU
  }
  dependsOn: [
    resourceGroups
  ]
}
