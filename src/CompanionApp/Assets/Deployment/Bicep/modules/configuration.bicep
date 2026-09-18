targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings

resource configurationStore 'Microsoft.AppConfiguration/configurationStores@2024-05-01' = {
  name: settings.APPCONFIG_NAME
  location: settings.LOCATION
  sku: {
    name: settings.APPCONFIG_SKU
  }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    dataPlaneProxy: {
      authenticationMode: 'Pass-through'
    }
  }
}

output configurationStoreId string = configurationStore.id
output endpoint string = configurationStore.properties.endpoint
