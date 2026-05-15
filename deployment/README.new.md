# SimpleL7Proxy — Deployment (interactive)

![Target architecture](arch.png)

This is the **interactive deployment path** for SimpleL7Proxy. A single
script (`deploy.sh`) drives all six steps from a menu and returns to the
menu after each step so you can iterate.

> Looking for the original step-by-step bash recipe? See
> [legacy-readme.md](legacy-readme.md). Both paths run the same
> per-component scripts under the hood.

---

## TL;DR

```bash
cd deployment
cp deploy.parameters.example.sh deploy.parameters.sh
vi deploy.parameters.sh        # edit the values listed below
./deploy.sh                    # interactive menu
```

The menu lets you run each step (and re-run any of them — every script is
idempotent):

```
1) Prerequisites              (Prereq/validate.sh)
2) Virtual Network            (VNet/deploy.sh)
3) Validate/Create ACR        (ContainerImage/validate-acr.sh)
4) Build Container Image      (ContainerImage/deploy.sh)
5) Azure Container Apps       (proxy-with-sidecar/deploy.sh)
6) Private DNS                (DNS/deploy.sh)
7) App Configuration          (AppConfiguration/deploy.sh)
8) Blob Storage  (optional)   (BlobStorage/deploy.sh)
9) Create RequestAPI Function (RequestAPI/create.sh)
10) Deploy/Update RequestAPI  (RequestAPI/deploy.sh)
q) Quit
```

After each step the script reports success/failure and pauses so you can
read output, then returns to the menu.

---

## One config file for everything

All sub-scripts source the **single consolidated parameters file**
`deployment/deploy.parameters.sh`. You only edit it once.

Copy the example:

```bash
cp deploy.parameters.example.sh deploy.parameters.sh
```

### Required edits

| Variable | Used by | Suggested value | Purpose |
|---|---|---|---|
| `LOCATION` | all | `eastus` | Azure region |
| `NETWORK_RESOURCE_GROUP` | VNet, DNS, ACA env | `rg-myapp-network` | RG that holds VNet, subnets, DNS zone, and the ACA environment |
| `CONTAINER_APP_RESOURCE_GROUP` | ACA, BlobStorage, AppConfiguration | `rg-myapp-prod` | RG that holds the Container App. Often the same as `NETWORK_RESOURCE_GROUP` |
| `STORAGE_RESOURCE_GROUP` | BlobStorage | `rg-myapp-storage` | RG for the storage account (only needed for async workflows) |
| `APPCONFIG_RESOURCE_GROUP` | AppConfiguration | `rg-myapp-appconfig` | RG for the App Configuration store |
| `ACR_NAME` | ContainerImage, ACA | `acrsimplel7proxy` | Azure Container Registry name (no `.azurecr.io` suffix). **Must be globally unique across Azure.** Validated by Step 3 before image builds |
| `PROXY_IMAGE_NAME` | ContainerImage, ACA | `simple-l7-proxy` | Repository name within ACR for the proxy image |
| `HEALTH_IMAGE_NAME` | ContainerImage, proxy-with-sidecar | `healthprobe` | Repository name within ACR for the health-probe image |
| `CONTAINER_APP_NAME` | ACA, proxy-with-sidecar, BlobStorage, AppConfiguration | `ca-myapp-proxy` | Name of the Container App |
| `ACA_ENVIRONMENT_NAME` | ACA | `cae-myapp` | Container Apps Environment name (VNet-integrated) |
| `HOST1` | ACA, proxy-with-sidecar | `host=https://your-api.azure-api.net;mode=apim;path=/;probe=/health` | Primary backend descriptor |

 > [!NOTE]
> **ACR validation:** Run Step 3 before building images.
> Step 3 (`ContainerImage/validate-acr.sh`) checks whether `ACR_NAME` exists and asks before creating
> it if missing (using `ACR_SKU`, defaulting to `Basic`). ACR names must be **globally unique** across
> all of Azure. If the default `acrsimplel7proxy` is taken, append a unique suffix (e.g.,
> `acrsimplel7proxy42`). Verify availability with:
> ```bash
> az acr check-name --name <your-acr-name>
> ```

### VNet and subnets

Edit only if the defaults overlap with existing networks in your subscription.

| Variable | Default | Purpose |
|---|---|---|
| `VNET_NAME` | `vnet-myapp` | Virtual network name |
| `VNET_ADDRESS_PREFIX` | `10.40.0.0/16` | VNet CIDR |
| `SUBNET_ACA_NAME` / `SUBNET_ACA_PREFIX` | `snet-aca` / `10.40.0.0/23` | Required by the ACA environment |
| `SUBNET_CLIENTVM_NAME` / `SUBNET_CLIENTVM_PREFIX` | `snet-clientvm` / `10.40.2.0/24` | Jumpbox / test client VMs |
| `SUBNET_AZUREFUNCTIONS_NAME` / `SUBNET_AZUREFUNCTIONS_PREFIX` | `snet-azurefunctions` / `10.40.3.0/24` | Optional Azure Functions integration |
| `SUBNET_APIM_NAME` / `SUBNET_APIM_PREFIX` | `snet-apim` / `10.40.4.0/24` | Optional APIM integration |
| `SUBNET_PRIVATEENDPOINTS_NAME` / `SUBNET_PRIVATEENDPOINTS_PREFIX` | `snet-privateendpoints` / `10.40.5.0/24` | Private endpoints |
| `DISABLE_PRIVATE_ENDPOINT_NETWORK_POLICIES` | `true` | Required for private endpoints |

### Container image build

| Variable | Default | Purpose |
|---|---|---|
| `BUILD_METHOD` | `remote` | `remote` builds in ACR (no Docker required). `local` requires Docker — **dev/test only** |
| `DOCKERFILE_PATH` | `SimpleL7Proxy/Dockerfile` | Dockerfile path under `src/` |
| `PROXY_VERSION_OVERRIDE` | *(empty)* | Override version tag; otherwise auto-extracted from `src/SimpleL7Proxy/Constants.cs` |
| `HEALTHPROBE_VERSION_OVERRIDE` | *(empty)* | Override health-probe tag; otherwise auto-extracted from `src/HealthProbe/Constants.cs` |
| `ACR_SKU` | `Basic` | SKU for ACR if Step 3 creates it (`Basic` / `Standard` / `Premium`) |

### Private DNS

| Variable | Default | Purpose |
|---|---|---|
| `DNS_ZONE_NAME` | `internal.contoso.com` | Private DNS zone, linked to the VNet |
| `ACA_INTERNAL_FQDN` | *(empty)* | Set after Step 5 deploys (e.g. `ca-myapp-proxy.internal.eastus.azurecontainerapps.io`) |
| `ACA_RECORD_NAME` | `ca-myapp-proxy` | Short CNAME pointing to `ACA_INTERNAL_FQDN` |
| `APIM_PRIVATE_IP` | *(empty)* | Set if APIM is in `snet-apim` |
| `APIM_RECORD_NAME` | `apim` | A record for APIM |

### Container App resources & ingress

| Variable | Default | Purpose |
|---|---|---|
| `CPU` / `MEMORY` | `0.5` / `1.0Gi` | ACA single-container resource size |
| `MIN_REPLICAS` / `MAX_REPLICAS` | `1` / `5` | Autoscale bounds |
| `INGRESS_VISIBILITY` | `Internal` | `Internal` (VNet only) or `External` |
| `INGRESS_PORT` | `8000` | Proxy listening port |
| `ENABLE_MANAGED_IDENTITY` | `true` | System-assigned managed identity (required for ACR pull, App Configuration, Blob Storage) |
| `ENABLE_APP_INSIGHTS` | `true` | Wires the ACA env to Log Analytics |
| `LOG_ANALYTICS_WORKSPACE_NAME` | `log-myapp` | Created if `ENABLE_APP_INSIGHTS=true` |

### Sidecar variant (Step 5 — `proxy-with-sidecar/deploy.sh`)

The sidecar deployment runs the proxy + a `HealthProbe` container in the
same Container App.

| Variable | Default | Purpose |
|---|---|---|
| `ENVIRONMENT_NAME` | `myapp-env` | ACA environment for the sidecar variant |
| `WEB_CPU` / `WEB_MEMORY` | `0.5` / `1.0` | Proxy container resources |
| `HEALTH_CPU` / `HEALTH_MEMORY` | `0.25` / `0.5` | Health-probe sidecar resources |
| `WEB_PORT` | `8000` | Proxy port |
| `HEALTH_PORT` | `9000` | Health-probe port |
| `INGRESS_TYPE` | `external` | `external` or `internal` (sidecar variant only) |
| `ENABLE_HTTPS` | `true` | Terminate TLS at the ACA ingress |
| `REVISION_MODE` | `single` | `single` or `multiple` |

### Blob Storage (Step 8 — async only)

| Variable | Default | Purpose |
|---|---|---|
| `STORAGE_ACCOUNT_NAME` | `myappstorage` | Account name. **Must be globally unique across Azure.** Created automatically by Step 8 if it doesn't exist. Use 3-24 lowercase letters and numbers only |
| `STORAGE_SKU` | `Standard_LRS` | `Standard_LRS` / `Standard_GRS` / `Standard_ZRS` / `Standard_RAGRS` |
| `CREATE_CONTAINERS` | `true` | Create the blob containers below |
| `BLOB_CONTAINERS` | `templates simplel7proxy` | Space-separated container names |
| `CA_BLOB_ROLE` | `Storage Blob Data Contributor` | Role granted to the Container App's managed identity |

> [!NOTE]
> **Blob Storage auto-creation:** You do not need to create the storage account before deploying.
> Step 8 (`BlobStorage/deploy.sh`) checks whether `STORAGE_ACCOUNT_NAME` exists in `STORAGE_RESOURCE_GROUP`
> and creates it automatically if it doesn't. However, storage account names must be **globally unique**
> across all of Azure and can only contain lowercase letters and numbers. If the default `myappstorage`
> is taken, append a unique suffix (e.g., `myappstorage42`). Verify availability with:
> ```bash
> az storage account check-name --name <yourstorageaccountname>
> ```

### App Configuration (Step 7)

| Variable | Default | Purpose |
|---|---|---|
| `APPCONFIG_NAME` | `myapp-appcfg` | Store name. **Must be globally unique across Azure.** Created automatically by Step 7 if it doesn't exist |
| `APPCONFIG_SKU` | `standard` | `standard` or `free` |
| `APPCONFIG_LABEL` | *(empty)* | Optional label for `Warm:*` keys |
| `AZURE_APPCONFIG_REFRESH_SECONDS` | `30` | Hot-reload interval written to `Warm:RefreshSeconds` |
| `UPDATE_CONTAINER_APP_ENV` | `true` | Push `AZURE_APPCONFIG_*` env vars onto the Container App |

> [!NOTE]
> **App Configuration auto-creation:** You do not need to create the App Configuration store before deploying.
> Step 7 (`AppConfiguration/deploy.sh`) checks whether `APPCONFIG_NAME` exists in `APPCONFIG_RESOURCE_GROUP`
> and creates it automatically if it doesn't. However, App Configuration names must be **globally unique**
> across all of Azure. If the default `myapp-appcfg` is taken, append a unique suffix
> (e.g., `myapp-appcfg42`) and rerun Step 7; Azure validates availability during creation.

### Auto-computed (do not edit)

These are derived at the bottom of `deploy.parameters.sh`:

- `REGISTRY_SERVER` = `${ACR_NAME}.azurecr.io`
- `PROXY_VERSION` / `HEALTHPROBE_VERSION` — extracted from `Constants.cs`
- `PROXY_IMAGE` / `HEALTH_IMAGE` — full `<registry>/<repo>:<tag>` references
- `WEB_IMAGE` / `IMAGE_NAME` — backwards-compat aliases for `PROXY_IMAGE`

---

## Step reference

Each menu entry just `cd`s into the matching folder and runs its
`deploy.sh` / `validate.sh`. The detailed READMEs in each
folder explain the underlying behavior.

| # | Folder | Script | What it does |
|---|---|---|---|
| 1 | `Prereq/`             | `validate.sh` | Verifies `az`, `jq`, `python3`, Bash, and active Azure login |
| 2 | `VNet/`               | `deploy.sh`   | Creates VNet + subnets in `NETWORK_RESOURCE_GROUP` |
| 3 | `ContainerImage/`     | `validate-acr.sh` | Validates ACR and asks before creating it if missing |
| 4 | `ContainerImage/`     | `deploy.sh`   | Builds proxy and health-probe images and tags them with versions from `Constants.cs` |
| 5 | `proxy-with-sidecar/` | `deploy.sh`   | Deploys ACA env + Container App with proxy + health-probe sidecar |
| 6 | `DNS/`                | `deploy.sh`   | Creates private DNS zone, VNet link, and CNAME → ACA FQDN |
| 7 | `AppConfiguration/`   | `deploy.sh`   | Creates App Configuration store and seeds `Warm:*` / `Cold:*` keys |
| 8 | `BlobStorage/`        | `deploy.sh`   | (optional, async only) creates storage account + blob containers + role assignment |

There is also an `ACA/deploy.sh` (proxy-only, no sidecar) — **dev/test
only**, exposed only via the legacy README.

---

## Production order

```
1 → 2 → 3 → 4 → 5 → 6 → 7   (+ 8 for async workflows)
```

Re-running any step is safe (every script is idempotent).

---

## Day-2 operations

| Task | Steps |
|---|---|
| Update backend config without redeploy | menu → `7` |
| Roll a new proxy version | menu → `3`, then `4`, then `5` |
| Add a DNS record | edit `deploy.parameters.sh`, menu → `6` |
| Tear it all down | `az group delete -n <NETWORK_RESOURCE_GROUP>` (and the others if separate) |

See [DAY2_OPERATIONS.md](DAY2_OPERATIONS.md) for the full day-2 reference.

---

## Troubleshooting

**Check in this order:**

1. `az containerapp logs show -n "${CONTAINER_APP_NAME}" -g "${CONTAINER_APP_RESOURCE_GROUP}"`
2. `az acr repository show-tags -n "${ACR_NAME}" --repository "${PROXY_IMAGE_NAME}"`
3. TCP reachability to backend from inside `snet-aca` (curl/nc from a client VM in `snet-clientvm`)
4. `nslookup "${ACA_RECORD_NAME}.${DNS_ZONE_NAME}"` from inside the VNet

**`deploy.parameters.sh not found`:**
```bash
cp deploy.parameters.example.sh deploy.parameters.sh
```

**Subnet not found:**
```bash
az network vnet subnet list --vnet-name "${VNET_NAME}" -g "${NETWORK_RESOURCE_GROUP}" -o table
```

**Container App stuck pulling image:**
```bash
az containerapp logs show -n "${CONTAINER_APP_NAME}" -g "${CONTAINER_APP_RESOURCE_GROUP}" --type system
az acr repository show-tags -n "${ACR_NAME}" --repository "${PROXY_IMAGE_NAME}"
```

**DNS not resolving:**
```bash
az network private-dns link vnet list -g "${NETWORK_RESOURCE_GROUP}" -z "${DNS_ZONE_NAME}"
az network private-dns record-set list -g "${NETWORK_RESOURCE_GROUP}" -z "${DNS_ZONE_NAME}" -o table
```

---

## File layout

```
deployment/
├── deploy.sh                       # interactive menu (this README)
├── deploy.parameters.example.sh    # consolidated config template
├── deploy.parameters.sh            # your edited values (gitignored)
├── README.md                       # this file
├── legacy-readme.md                # original step-by-step instructions
├── DAY2_OPERATIONS.md
├── Prereq/
├── VNet/
├── ContainerImage/
├── ACA/                            # dev/test only (proxy without sidecar)
├── proxy-with-sidecar/             # production
├── DNS/
├── AppConfiguration/
└── BlobStorage/
```

---

## References

- [Day-2 Operations](DAY2_OPERATIONS.md)
- [App Configuration keys](../docs/AZURE_APP_CONFIGURATION.md)
- [Backend hosts](../docs/BACKEND_HOSTS.md)
- [Health checking](../docs/HEALTH_CHECKING.md)
- [Troubleshooting](../docs/TroubleshootTOC.md)
- [Configuration reference](../docs/CONFIGURATION_SETTINGS.md)
- [Advanced scenarios](../docs/ADVANCED_DEVELOPMENT.md)
