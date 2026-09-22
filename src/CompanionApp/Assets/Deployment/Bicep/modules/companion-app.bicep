targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

@description('Deployment Setup values used to create the Companion App service.')
param settings DeploymentSettings
param usePrivateRegistry bool
param environmentId string
param appConfigurationEndpoint string

var companionImage = usePrivateRegistry ? '${registry.properties.loginServer}/${settings.COMPANION_IMAGE_NAME}:v2.3.0' : 'publicnvmacr.azurecr.io/companionapp:v2.3.0'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: settings.ACR_NAME
  scope: resourceGroup(settings.CONTAINER_APP_RESOURCE_GROUP)
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: settings.COMPANION_APP_NAME
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
        external: true
        targetPort: 8080
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
          name: 'companion'
          image: companionImage
          env: [
            {
              name: 'CompanionApp__AppConfigurationEndpoint'
              value: appConfigurationEndpoint
            }
            {
              name: 'CompanionApp__AppConfigurationLabel'
              value: settings.APPCONFIG_LABEL
            }
          ]
          resources: {
            cpu: json(settings.WEB_CPU)
            memory: '${settings.WEB_MEMORY}Gi'
          }
        }
      ]
      scale: {
        minReplicas: settings.MIN_REPLICAS
        maxReplicas: settings.MAX_REPLICAS
        rules: [
          {
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '100'
              }
            }
          }
        ]
      }
    }
  }
}

output appId string = app.id
output identityPrincipalId string = app.identity.principalId
output url string = 'https://${app.properties.configuration.ingress.fqdn}'
