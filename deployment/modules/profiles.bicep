targetScope = 'resourceGroup'

param location string
param storageAccountName string
param environmentId string
param appPrincipalIds array = []

var contributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource profiles 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = {
  name: '${storageAccount.name}/default/profiles'
}

resource seedJob 'Microsoft.App/jobs@2024-03-01' = {
  name: 'profiles-${uniqueString(storageAccount.id)}'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 1200
      replicaRetryLimit: 0
      scheduleTriggerConfig: {
        cronExpression: '*/5 * * * *'
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'seed-profiles'
          image: 'python:3.13.7-slim'
          command: [
            'python'
            '-c'
            loadTextContent('../assets/seed-profiles.py')
          ]
          env: [
            {
              name: 'PROFILE_SAMPLE_BASE64'
              value: base64(loadTextContent('../assets/config.json'))
            }
            {
              name: 'PROFILE_BLOB_URL'
              value: '${storageAccount.properties.primaryEndpoints.blob}profiles/config.json'
            }
            {
              name: 'STORAGE_AUDIENCE'
              value: storageAccount.properties.primaryEndpoints.blob
            }
          ]
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
    }
  }
}

resource seedAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(profiles.id, seedJob.id, contributorRoleId)
  scope: profiles
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: seedJob.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principalId in appPrincipalIds: {
  name: guid(profiles.id, principalId, contributorRoleId)
  scope: profiles
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}]

output jobName string = seedJob.name
output jobResourceGroup string = resourceGroup().name
