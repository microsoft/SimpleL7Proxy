targetScope = 'resourceGroup'

param location string
param registryName string
param registrySku 'Basic' | 'Standard' | 'Premium'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  sku: {
    name: registrySku
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}
