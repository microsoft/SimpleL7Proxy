targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings

resource eventHubNamespace 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: settings.COMPANION_EVENTHUB_NAMESPACE
  location: settings.LOCATION
  sku: {
    name: 'Standard'
    tier: 'Standard'
    capacity: 1
  }
  properties: {
    minimumTlsVersion: '1.2'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource eventHub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  parent: eventHubNamespace
  name: settings.COMPANION_EVENTHUB_NAME
  properties: {
    partitionCount: 2
    messageRetentionInDays: 1
  }
}

resource consumerGroup 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = {
  parent: eventHub
  name: settings.COMPANION_EVENTHUB_CONSUMER_GROUP
  properties: {}
}
