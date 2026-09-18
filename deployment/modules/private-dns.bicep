targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param environmentDomain string
param environmentStaticIp string
param proxyFqdn string

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-01-01' existing = {
  name: settings.VNET_NAME
}

resource applicationZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: settings.DNS_ZONE_NAME
  location: 'global'
}

resource applicationLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  name: '${settings.VNET_NAME}-link'
  parent: applicationZone
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource applicationRecord 'Microsoft.Network/privateDnsZones/CNAME@2020-06-01' = {
  name: settings.ACA_RECORD_NAME
  parent: applicationZone
  properties: {
    ttl: 300
    cnameRecord: {
      cname: empty(settings.ACA_INTERNAL_FQDN) ? proxyFqdn : settings.ACA_INTERNAL_FQDN
    }
  }
}

resource apimRecord 'Microsoft.Network/privateDnsZones/A@2020-06-01' = if (!empty(settings.APIM_PRIVATE_IP)) {
  name: settings.APIM_RECORD_NAME
  parent: applicationZone
  properties: {
    ttl: 300
    aRecords: [
      {
        ipv4Address: settings.APIM_PRIVATE_IP
      }
    ]
  }
}

resource environmentZone 'Microsoft.Network/privateDnsZones@2020-06-01' = {
  name: environmentDomain
  location: 'global'
}

resource environmentLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = {
  name: 'environment-link'
  parent: environmentZone
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: virtualNetwork.id
    }
  }
}

resource environmentWildcard 'Microsoft.Network/privateDnsZones/A@2020-06-01' = {
  name: '*'
  parent: environmentZone
  properties: {
    ttl: 300
    aRecords: [
      {
        ipv4Address: environmentStaticIp
      }
    ]
  }
}
