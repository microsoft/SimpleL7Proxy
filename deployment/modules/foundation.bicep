targetScope = 'resourceGroup'

import { DeploymentSettings } from '../types.bicep'

param settings DeploymentSettings
param acaSubnetId string
param existingEnvironmentId string
param existingEnvironmentDomain string
param existingEnvironmentStaticIp string

var workspaceId = resourceId('Microsoft.OperationalInsights/workspaces', settings.LOG_ANALYTICS_WORKSPACE_NAME)
var insightsId = resourceId('Microsoft.Insights/components', '${settings.CONTAINER_APP_NAME}-insights')

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (settings.ENABLE_APP_INSIGHTS) {
  name: settings.LOG_ANALYTICS_WORKSPACE_NAME
  location: settings.LOCATION
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = if (settings.ENABLE_APP_INSIGHTS) {
  name: '${settings.CONTAINER_APP_NAME}-insights'
  location: settings.LOCATION
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = if (!settings.USE_EXISTING_ENVIRONMENT) {
  name: settings.ENVIRONMENT_NAME
  location: settings.LOCATION
  properties: union(
    {
      workloadProfiles: [
        {
          name: 'Consumption'
          workloadProfileType: 'Consumption'
        }
      ]
    },
    settings.ENABLE_APP_INSIGHTS ? {
      appLogsConfiguration: {
        destination: 'log-analytics'
        logAnalyticsConfiguration: {
          customerId: reference(workspaceId, '2023-09-01').customerId
          sharedKey: listKeys(workspaceId, '2023-09-01').primarySharedKey
        }
      }
    } : {},
    settings.PRIVATE_NETWORK_DEPLOYMENT ? {
      vnetConfiguration: {
        infrastructureSubnetId: acaSubnetId
        internal: true
      }
    } : {}
  )
  dependsOn: settings.ENABLE_APP_INSIGHTS ? [
    workspace
  ] : []
}

output environmentId string = environment.?id ?? existingEnvironmentId
output environmentDomain string = environment.?properties.defaultDomain ?? existingEnvironmentDomain
output environmentStaticIp string = environment.?properties.staticIp ?? existingEnvironmentStaticIp
output appInsightsConnectionString string = settings.ENABLE_APP_INSIGHTS ? reference(insightsId, '2020-02-02').ConnectionString : ''
