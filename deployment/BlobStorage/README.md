# Azure Blob Storage Deployment

Provisions the Azure Storage account that SimpleL7Proxy uses for **async-mode**
artifacts (response/header blobs) and for the **templates** container that
holds the async response templates (`welcome.json`, `notready.json`,
`notauthorized.json`).

The script focuses on **storage account setup and the Container App's
connection to it** — it does not touch App Configuration, Service Bus, or
the Container App's environment variables.

## What the Script Does

1. **Reads the live Container App** to obtain (or create) its
   system-assigned managed identity.
2. **Creates the resource group and storage account** if they don't
   already exist:
   - `kind=StorageV2`
   - `--allow-blob-public-access true`
   - `--public-network-access Enabled` (required for the Container App to
     reach the blob endpoint)
   - `--min-tls-version TLS1_2`
3. **Assigns RBAC** to the Container App's managed identity on the storage
   account scope. Default role: **`Storage Blob Data Contributor`** — the
   proxy both reads templates and writes async result blobs, so Reader is
   not sufficient.
4. **Optionally creates blob containers** (`templates`, `simplel7proxy`)
   when `CREATE_CONTAINERS=true`. Container creation uses
   `--auth-mode login`, so the signed-in user is JIT-granted
   `Storage Blob Data Contributor` on the account first (with a 30 s wait
   for RBAC propagation). This works whether or not shared-key auth is
   enabled on the account.

The script is **idempotent** — re-running it skips work that's already done.

## Prerequisites

| Requirement | Details |
|---|---|
| **Azure CLI** | `az` ≥ 2.50 with the `containerapp` extension |
| **jq** | Used to parse the Container App JSON |
| **Azure login** | `az login` (the script will prompt if needed) |
| **A running Container App** | The script reads its identity and enables it if absent |
| **Bash 4+** | Uses `${VAR,,}` lowercase expansion |

## Quick Start

```bash
cd deployment/BlobStorage

# 1. Create your parameters file
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh with your values
#    (see Parameters section below)

# 3. Run
./deploy.sh
```

## Parameters

All parameters are set in `deploy.parameters.sh`.

### Required

| Parameter | Description |
|---|---|
| `CONTAINER_APP_NAME` | Container App that will read/write blobs using its managed identity |
| `CONTAINER_APP_RESOURCE_GROUP` | Resource group where the Container App lives |
| `RESOURCE_GROUP` | Resource group for the storage account (created if missing) |
| `LOCATION` | Azure region for the storage account |
| `STORAGE_ACCOUNT_NAME` | Globally unique storage account name (3–24 lowercase alphanumeric) |

### Optional

| Parameter | Default | Description |
|---|---|---|
| `STORAGE_SKU` | `Standard_LRS` | Storage replication SKU. Short forms (`lrs`, `grs`, `zrs`, `ragrs`) are normalized. |
| `CREATE_CONTAINERS` | `false` | When `true`, creates the containers listed in `BLOB_CONTAINERS`. |
| `BLOB_CONTAINERS` | `templates simplel7proxy` | Space-separated list of containers to create when `CREATE_CONTAINERS=true`. |
| `CA_BLOB_ROLE` | `Storage Blob Data Contributor` | Role assigned to the Container App's managed identity on the storage account. The proxy writes blobs, so Reader is not enough. |

> **Do not commit `deploy.parameters.sh`** — it contains environment-specific values.
> Only `deploy.parameters.example.sh` is checked in.

## Containers Used by the Proxy

| Container | Purpose |
|---|---|
| `templates` | Holds async-mode response JSON templates (`welcome.json`, `notready.json`, `notauthorized.json`). Loaded once at startup by `TemplateLoader`. |
| `simplel7proxy` | Default `data` container for async result/header blobs (override per user via the `async-config` user-profile field). |

After the script creates the `templates` container, you must upload the
template files yourself:

```bash
az storage blob upload-batch \
    --account-name "${STORAGE_ACCOUNT_NAME}" \
    --destination templates \
    --source ../../src/SimpleL7Proxy/templates \
    --auth-mode login \
    --overwrite
```

## How the Proxy Connects

The proxy reads its blob endpoint from the `AsyncBlobStorageConfig`
setting (in App Configuration or as an env var). Two formats are
accepted:

1. **Comma-separated** (managed identity):
   ```
   blobserviceuri=https://<account>.blob.core.windows.net, useMI=true
   ```
2. **Raw portal connection string** (key-based, treated as
   `useMI=false`):
   ```
   DefaultEndpointsProtocol=https;AccountName=<account>;AccountKey=...;EndpointSuffix=core.windows.net
   ```

This script's role assignment makes option (1) work: the Container App's
managed identity gets `Storage Blob Data Contributor` on the storage
account, and the proxy authenticates with `DefaultAzureCredential`.

## RBAC Notes

- **Container App MI** — assigned `${CA_BLOB_ROLE}` (default
  `Storage Blob Data Contributor`) on the storage account scope.
- **Signed-in user** — only when `CREATE_CONTAINERS=true`, the script JIT
  assigns `Storage Blob Data Contributor` to the operator running it so
  that `az storage container create --auth-mode login` succeeds. This
  assignment is **not removed** by the script; remove it manually if your
  org's policy requires it.
- RBAC propagation can take a few minutes. The script sleeps 30 s after
  granting the operator role; if subsequent commands still 403, wait and
  re-run.

## Re-running

The script is idempotent:

- Existing resource group / storage account / containers are reused.
- Existing role assignments are detected and skipped.
- Safe to run repeatedly to verify state.

## Cleanup

```bash
az storage account delete \
    --name "${STORAGE_ACCOUNT_NAME}" \
    --resource-group "${RESOURCE_GROUP}" \
    --yes
```

Role assignments scoped to the storage account are removed automatically
when the account is deleted.
