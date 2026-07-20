# Async Blob Storage: Retention and Lifecycle Management

| Attribute | Value |
|-----------|-------|
| **Version** | 1.2 |
| **Last Updated** | 2026-07-16 |
| **Owner** | SimpleL7Proxy maintainers |
| **Review Cycle** | Quarterly |

## Summary

SimpleL7Proxy writes async results to Azure Blob Storage. Blobs do not expire automatically. Operators MUST configure an external Azure Storage lifecycle management policy with an operator-selected retention window; the proxy does not configure or enforce blob deletion.

> **TL;DR**
> - The retention period is an Azure Storage lifecycle-policy value, not a SimpleL7Proxy setting.
> - A lifecycle management policy MUST target every result container, including `StorageDbContainerName` (default: `Requests`) and any per-user containers.
> - Three equivalent provisioning methods are available: Azure Portal, Azure CLI, and Bicep.

> [!WARNING]
> `AsyncTTLSecs` controls async request expiration, not blob retention. The Azure Storage lifecycle management policy MUST be created independently in the storage account.

---

## Scope & Applicability

**In scope:** Lifecycle management policy configuration for async request blobs written by SimpleL7Proxy.
**Out of scope:** Blob Storage authentication and connection string setup (see [AsyncOperation.md](AsyncOperation.md)); blob container creation (the proxy creates the container automatically at startup).
**Dependencies:** `AsyncModeEnabled=true`; `AsyncBlobStorageConfig` set with a valid storage account URI.

---

## Reference — Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `StorageDbContainerName` | `Requests` | Default container name for async result blobs. A lifecycle rule MUST target this and any per-user container names. |

The examples below use an operator-selected retention period of `7` days. This value exists only in the Azure Storage lifecycle policy.

> [!NOTE]
> Azure Blob Storage lifecycle management policies run once per day. Blobs are not deleted immediately when the retention period elapses — deletion occurs on the next daily policy evaluation.

---

## Configuring Blob Storage Lifecycle Management

The following three methods are equivalent. Operators MUST use exactly one.

### Option 1: Azure Portal

1. Navigate to your storage account in the [Azure Portal](https://portal.azure.com).
2. Select **Lifecycle Management** under **Data management**.
3. Create a new rule with these settings:
   - **Rule name**: `DeleteExpiredAsyncBlobs`
   - **Rule scope**: Apply to every async result container (default container: `Requests`)
   - **If blob was last modified more than (days ago)**: the operator-selected retention period (example: `7`)
   - **Then delete the blob**: Checked

### Option 2: Azure CLI

```bash
STORAGE_ACCOUNT="your-storage-account-name"
RESOURCE_GROUP="your-resource-group"
CONTAINER_NAME="Requests"
RETENTION_DAYS=7

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
EOF
```

### Option 3: Bicep

```bicep
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

---

## Validation & Compliance

| Check | Method | Expected Result |
|-------|--------|-----------------|
| Lifecycle policy exists | Azure Portal → Storage Account → Lifecycle Management | `DeleteExpiredAsyncBlobs` rule is present and enabled |
| Target container correct | Inspect rule's prefix filter | Matches `StorageDbContainerName` value exactly |
| Blobs deleted after retention | Wait for the lifecycle policy's configured period after async requests complete | Blobs absent from container on next daily evaluation |
| Container exists | Azure Portal → Storage Account → Containers | `{StorageDbContainerName}` container is present |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.2 | 2026-07-16 | Clarified that retention is controlled only by an external Azure Storage lifecycle policy; removed the nonexistent proxy `BlobRetentionDays` setting. | SimpleL7Proxy maintainers |
| 1.1 | 2026-05-21 | Added H1 title, metadata, TL;DR, Summary, Scope & Applicability, Validation & Compliance, Version History; fixed Bicep code block language tag; standardized variable table | SimpleL7Proxy maintainers |
| 1.0 | — | Initial version (no document title or intro) | SimpleL7Proxy maintainers |