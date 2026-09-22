targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param usePrivateRegistry bool
param environmentId string

var metricsImage = usePrivateRegistry ? '${registry.properties.loginServer}/metricsserver:v1.0.0' : 'publicnvmacr.azurecr.io/metricsserver:v1.0.0'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: settings.ACR_NAME
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: settings.METRICS_SERVER_NAME
  location: settings.LOCATION
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: union({
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 9100
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
    }, usePrivateRegistry ? {
      registries: [
        {
          server: registry.properties.loginServer
          identity: 'system'
        }
      ]
    } : {})
    template: {
      containers: [
        {
          name: 'metrics'
          image: metricsImage
          resources: {
            cpu: json(settings.METRICS_CPU)
            memory: '${settings.METRICS_MEMORY}Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output appId string = app.id
output identityPrincipalId string = app.identity.principalId
