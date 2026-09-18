targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: settings.VNET_NAME
  location: settings.LOCATION
  properties: {
    addressSpace: {
      addressPrefixes: [
        settings.VNET_ADDRESS_PREFIX
      ]
    }
    subnets: [
      {
        name: settings.SUBNET_ACA_NAME
        properties: {
          addressPrefix: settings.SUBNET_ACA_PREFIX
          delegations: [
            {
              name: 'service-delegation'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: settings.SUBNET_CLIENTVM_NAME
        properties: {
          addressPrefix: settings.SUBNET_CLIENTVM_PREFIX
        }
      }
      {
        name: settings.SUBNET_AZUREFUNCTIONS_NAME
        properties: {
          addressPrefix: settings.SUBNET_AZUREFUNCTIONS_PREFIX
          delegations: settings.ASYNC_DEPLOYMENT ? [
            {
              name: 'service-delegation'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ] : []
        }
      }
      {
        name: settings.SUBNET_APIM_NAME
        properties: {
          addressPrefix: settings.SUBNET_APIM_PREFIX
        }
      }
      {
        name: settings.SUBNET_PRIVATEENDPOINTS_NAME
        properties: {
          addressPrefix: settings.SUBNET_PRIVATEENDPOINTS_PREFIX
          privateEndpointNetworkPolicies: settings.DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES ? 'Disabled' : 'Enabled'
        }
      }
    ]
  }
}

output acaSubnetId string = '${virtualNetwork.id}/subnets/${settings.SUBNET_ACA_NAME}'
output functionsSubnetId string = '${virtualNetwork.id}/subnets/${settings.SUBNET_AZUREFUNCTIONS_NAME}'
