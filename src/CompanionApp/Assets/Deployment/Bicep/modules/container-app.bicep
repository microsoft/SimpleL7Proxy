targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param usePrivateRegistry bool
param environmentId string
param appInsightsConnectionString string
param appConfigurationEndpoint string
param blobEndpoint string
param requestApiHostName string

var proxyImage = usePrivateRegistry ? '${registry.properties.loginServer}/${settings.PROXY_IMAGE_NAME}:v2.3.0' : 'publicnvmacr.azurecr.io/simplel7proxy@sha256:2ebaff3e90fc9162421f08f8095a030627c4aea7046b3da59a8ea7720e4c530f'
var healthImage = usePrivateRegistry ? '${registry.properties.loginServer}/${settings.HEALTH_IMAGE_NAME}:v2.0.1' : 'publicnvmacr.azurecr.io/healthprobe@sha256:e28a0bd8555d97ec800f80cb37201785e4ceb9c783fbedf679de5145e3c689d1'
var baseEnvironment = [
  {
    name: 'HealthProbeSidecar'
    value: 'Enabled=${settings.HEALTHPROBE_TYPE == 'sidecar'};url=http://localhost:${settings.HEALTH_PORT}'
  }
]
var insightsEnvironment = settings.ENABLE_APP_INSIGHTS ? [
  {
    name: 'APPINSIGHTS_CONNECTIONSTRING'
    value: appInsightsConnectionString
  }
] : []
var asyncEnvironment = settings.ASYNC_DEPLOYMENT ? [
  {
    name: 'AsyncModeEnabled'
    value: 'true'
  }
  {
    name: 'AsyncBlobStorageConfig'
    value: 'uri=${blobEndpoint},mi=true'
  }
  {
    name: 'AsyncSBConfig'
    value: 'ns=${settings.REQUESTAPI_SERVICEBUS_NAMESPACE},q=${settings.REQUESTAPI_SERVICEBUS_QUEUE},mi=true'
  }
  {
    name: 'RequestAPIBaseUri'
    value: 'https://${requestApiHostName}/api/'
  }
] : []
var appConfigurationEnvironment = settings.UPDATE_CONTAINER_APP_ENV ? [
  {
    name: 'AZURE_APPCONFIG_ENDPOINT'
    value: appConfigurationEndpoint
  }
  {
    name: 'AZURE_APPCONFIG_LABEL'
    value: settings.APPCONFIG_LABEL
  }
  {
    name: 'AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS'
    value: string(settings.AZURE_APPCONFIG_REFRESH_INTERVAL_SECONDS)
  }
] : []
var healthContainer = settings.HEALTHPROBE_TYPE == 'sidecar' ? [
  {
    name: 'health'
    image: healthImage
    env: [
      {
        name: 'HEALTHPROBE_PORT'
        value: string(settings.HEALTH_PORT)
      }
    ]
    resources: {
      cpu: json(settings.HEALTH_CPU)
      memory: '${settings.HEALTH_MEMORY}Gi'
    }
    probes: [
      {
        type: 'Liveness'
        httpGet: {
          path: '/liveness'
          port: settings.HEALTH_PORT
        }
        initialDelaySeconds: 5
        periodSeconds: 10
        timeoutSeconds: 10
        successThreshold: 1
        failureThreshold: 30
      }
      {
        type: 'Readiness'
        httpGet: {
          path: '/readiness'
          port: settings.HEALTH_PORT
        }
        initialDelaySeconds: 5
        periodSeconds: 10
        timeoutSeconds: 10
        successThreshold: 1
        failureThreshold: 30
      }
      {
        type: 'Startup'
        httpGet: {
          path: '/startup'
          port: settings.HEALTH_PORT
        }
        initialDelaySeconds: 5
        periodSeconds: 10
        timeoutSeconds: 10
        successThreshold: 1
        failureThreshold: 30
      }
    ]
  }
] : []

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: settings.ACR_NAME
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: settings.CONTAINER_APP_NAME
  location: settings.LOCATION
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: union({
      activeRevisionsMode: settings.REVISION_MODE
      ingress: {
        external: settings.INGRESS_TYPE == 'external'
        targetPort: settings.WEB_PORT
        transport: 'auto'
        allowInsecure: !settings.ENABLE_HTTPS
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
      terminationGracePeriodSeconds: settings.TERMINATION_GRACE_PERIOD_SECONDS
      containers: concat([
        {
          name: 'proxy'
          image: proxyImage
          env: concat(baseEnvironment, insightsEnvironment, asyncEnvironment, appConfigurationEnvironment)
          resources: {
            cpu: json(settings.WEB_CPU)
            memory: '${settings.WEB_MEMORY}Gi'
          }
        }
      ], healthContainer)
      scale: {
        minReplicas: settings.MIN_REPLICAS
        maxReplicas: settings.MAX_REPLICAS
        rules: [
          {
            name: 'http-scaling'
            http: {
              metadata: {
                concurrentRequests: '1000'
              }
            }
          }
        ]
      }
    }
  }
}

output containerAppId string = containerApp.id
output identityPrincipalId string = containerApp.identity.principalId
output proxyFqdn string = containerApp.properties.configuration.ingress.fqdn
