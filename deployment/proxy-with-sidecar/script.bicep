@description('Name of the existing Container App')
param containerAppName string

@description('Resource ID of the existing Container Apps environment (Microsoft.App/managedEnvironments)')
param managedEnvId string

@description('Location (must match the existing Container App location)')
param location string

@description('Container image for the web container')
param webImage string

@description('Container image for the health sidecar')
param healthImage string

@description('Optional registry server (e.g., myregistry.azurecr.io). Leave empty if using public images.')
param registryServer string = ''

@description('Web container CPU cores - e.g., 0.25, 0.5, 1.0, 2.0')
param webCpu string = '0.5'

@description('Web container memory in Gi - e.g., 0.5Gi, 1.0Gi, 2.0Gi')
param webMemory string = '1.0Gi'

@description('Health container CPU cores - e.g., 0.25, 0.5, 1.0')
param healthCpu string = '0.25'

@description('Health container memory in Gi - e.g., 0.5Gi, 1.0Gi')
param healthMemory string = '0.5Gi'

@description('Target port exposed by the web container (ingress)')
param webPort int = 8000

@description('Internal port exposed by the health container for probes')
param healthPort int = 9000

@description('Whether ingress should be external or internal')
@allowed([
  'external'
  'internal'
])
param ingressType string = 'external'

@description('Enable or disable HTTPS on ingress')
param enableHttps bool = true

@description('Revision mode; single is recommended for sidecars')
@allowed([
  'single'
  'multiple'
])
param revisionMode string = 'single'

@description('Timestamp for generating unique revision suffix')
param timestamp string = utcNow()

var registries = empty(registryServer) ? [] : [
  {
    server: registryServer
    identity: 'system'
  }
]

resource updateApp 'Microsoft.App/containerApps@2024-02-02-preview' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: managedEnvId
    configuration: {
      activeRevisionsMode: revisionMode
      registries: registries
      ingress: {
        external: ingressType == 'external'
        targetPort: webPort
        transport: 'auto'
        allowInsecure: !enableHttps
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
    }
    template: {
      revisionSuffix: uniqueString(resourceGroup().id, timestamp, webImage, healthImage)
      containers: [
        {
          name: 'proxy'
          image: webImage
          env: [
            {
              name: 'DEPLOY_TIMESTAMP'
              value: timestamp
            }
            {
              name: 'Workers'
              value: '100'
            }
            {
              name: 'EVENTHUB_NAME'
              value: 'nvmtreh'
            }
            {
              name: 'EVENTHUB_NAMESPACE'
              value: 'nvmtrehns'
            }
            {
              name: 'AsyncModeEnabled'
              value: 'false'
            }
            {
              name: 'APPINSIGHTS_CONNECTIONSTRING'
              value: 'InstrumentationKey=f618df7b-0b79-4685-9976-4adc5ff9e808;IngestionEndpoint=https://eastus-8.in.applicationinsights.azure.com/;LiveEndpoint=https://eastus.livediagnostics.monitor.azure.com/;ApplicationId=4fc39f85-e1d1-497e-918b-ad865352fbb8'
            }
            {
              name: 'Host1'
              value: 'https://nvmtr2apim.azure-api.net'
            }
            {
              name: 'Probe_path1'
              value: '/status-0123456789abcdef'
            }
          ]
          resources: {
            cpu: json(webCpu)
            memory: webMemory
          }
          probes: [
            {
              type: 'Readiness'
              httpGet: {
                path: '/'
                port: webPort
                httpHeaders: []
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              timeoutSeconds: 5
              successThreshold: 1
              failureThreshold: 3
            }
          ]
        }
        {
          name: 'health'
          image: healthImage
          env: [
            {
              name: 'HEALTHPROBE_PORT'
              value: string(healthPort)
            }
            {
              name: 'DEPLOY_TIMESTAMP'
              value: timestamp
            }
          ]
          resources: {
            cpu: json(healthCpu)
            memory: healthMemory
          }
          probes: [
            {
              type: 'Liveness'
              tcpSocket: {
                port: healthPort
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              timeoutSeconds: 10
              successThreshold: 1
              failureThreshold: 30
            }
            {
              type: 'Readiness'
              tcpSocket: {
                port: healthPort
              }
              initialDelaySeconds: 3
              periodSeconds: 10
              timeoutSeconds: 10
              successThreshold: 1
              failureThreshold: 30
            }
            {
              type: 'Startup'
              tcpSocket: {
                port: healthPort
              }
              initialDelaySeconds: 3
              periodSeconds: 10
              timeoutSeconds: 10
              successThreshold: 1
              failureThreshold: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 10
      }
    }
  }
}

@description('The FQDN of the Container App')
output fqdn string = updateApp.properties.configuration.ingress.fqdn

@description('The resource ID of the Container App')
output resourceId string = updateApp.id

@description('The latest revision name')
output latestRevisionName string = updateApp.properties.latestRevisionName
