### Blob Storage Lifecycle Management Variables

| Variable                     | Description                                                                                          | Default                                  |
| ---------------------------- | ---------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| **BlobRetentionDays**        | Number of days to retain blobs before automatic deletion. Set to 0 to disable automatic deletion.     | 7                                        |
| **AsyncBlobStorageContainer**| The container name where async request blobs are stored.                                              | asyncrequests                            |


## Configuring Blob Storage Lifecycle Management

SimpleL7Proxy creates blobs in Azure Storage for asynchronous requests. These blobs contain request data and headers, and by default, they don't expire automatically. To avoid storage costs from accumulating over time, configure Azure Blob Storage lifecycle management:

### Option 1: Using Azure Portal

1. Navigate to your storage account in the Azure Portal
2. Select **Lifecycle Management** under **Data management**
3. Create a new rule with these settings:
   - **Rule name**: DeleteExpiredAsyncBlobs
   - **Rule scope**: Apply to containers matching pattern: `{AsyncBlobStorageContainer}`
   - **If blob was last modified more than (days ago)**: `{BlobRetentionDays}` (default 7)
   - **Then delete the blob**: Checked

### Option 2: Using Azure CLI

```bash
# Set variables
STORAGE_ACCOUNT="your-storage-account-name"
RESOURCE_GROUP="your-resource-group"
CONTAINER_NAME="asyncrequests"  # Or your custom container name
RETENTION_DAYS=7  # Or your custom retention period

# Create lifecycle management policy
az storage account management-policy create \
  --account-name $STORAGE_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --policy @- << EOF
{
  "rules": [
    {
      "name": "DeleteExpiredAsyncBlobs",
      "type": "Lifecycle",
      "definition": {
        "filters": {
          "blobTypes": ["blockBlob"],
          "prefixMatch": ["$CONTAINER_NAME"]
        },
        "actions": {
          "baseBlob": {
            "delete": {
              "daysAfterModificationGreaterThan": $RETENTION_DAYS
            }
          }
        }
      }
    }
  ]
}

```

### Option 3: Using Azure Bicep/ARM Template

```json

resource storageAccount 'Microsoft.Storage/storageAccounts@2021-08-01' = {
  // ...existing storage account properties...
  
  resource managementPolicies 'managementPolicies' = {
    name: 'default'
    properties: {
      policy: {
        rules: [
          {
            name: 'DeleteExpiredAsyncBlobs'
            type: 'Lifecycle'
            definition: {
              filters: {
                blobTypes: [
                  'blockBlob'
                ]
                prefixMatch: [
                  containerName
                ]
              }
              actions: {
                baseBlob: {
                  delete: {
                    daysAfterModificationGreaterThan: retentionDays
                  }
                }
              }
            }
          }
        ]
      }
    }
  }
}

```