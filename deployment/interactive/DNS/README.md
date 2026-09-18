# Private DNS Zone Deployment

Provisions a private DNS zone linked to the VNet for internal service discovery. This enables services running inside the VNet (ACA, APIM, Functions, etc.) to resolve custom internal domain names.

After deploying the ACA, you can register its internal FQDN in this DNS zone so clients can use a human-readable name instead of the auto-generated Azure domain.

This folder follows the same deployment convention as `deployment/VNet` and `deployment/ACA`:

1. Copy `deploy.parameters.example.sh` to `deploy.parameters.sh`
2. Update values
3. Run `./deploy.sh`

## Prerequisites

| Requirement | Details |
|---|---|
| Azure CLI | `az` installed and authenticated |
| Bash | Linux/macOS shell, WSL, or Git Bash |
| VNet deployed | Run `deployment/VNet/deploy.sh` first |
| ACA deployed | Run `deployment/ACA/deploy.sh` first (for ACA FQDN) |
| Azure permissions | Permission to create DNS zones and records |

## Quick Start

```bash
cd deployment/DNS

# 1. Create your parameters file
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh with your values
#    (ensure RESOURCE_GROUP and VNET_NAME match VNet deployment)

# 3. Run
./deploy.sh
```

## Parameters

All parameters are set in `deploy.parameters.sh`.

| Parameter | Description |
|---|---|
| `RESOURCE_GROUP` | Must match the VNet resource group |
| `LOCATION` | Azure region (must match VNet) |
| `VNET_NAME` | VNet created by `deployment/VNet` |
| `DNS_ZONE_NAME` | Private DNS zone domain (e.g., `internal.contoso.com`) |
| `ACA_INTERNAL_FQDN` | Internal FQDN of ACA (e.g., `ca-myapp-proxy.internal.eastus.azurecontainerapps.io`) |
| `ACA_RECORD_NAME` | Short DNS name for ACA (e.g., `ca-myapp-proxy`) |
| `APIM_PRIVATE_IP` | Private IP of APIM instance (if deployed in VNet) |
| `APIM_RECORD_NAME` | Short DNS name for APIM (e.g., `apim`) |

> Do not commit `deploy.parameters.sh` with environment-specific values.

## What `deploy.sh` Does

1. Loads values from `deploy.parameters.sh`
2. Verifies Azure CLI and login
3. Retrieves the VNet ID
4. Creates the private DNS zone (if it doesn't exist)
5. Links the VNet to the DNS zone
6. Creates or updates DNS records:
   - **CNAME record** for ACA (maps short name to auto-generated FQDN)
   - **A record** for APIM (if private IP is provided)
7. Prints a summary of the deployment

## Private DNS Zone Explained

A private DNS zone allows services within the VNet to resolve domain names without exposing them to the public internet.

### Benefits

- **Internal-only resolution** — DNS records are only accessible from within the linked VNet
- **No public exposure** — Unlike public DNS, private records are not visible to external clients
- **Network isolation** — Complements the network security of your VNet
- **Easy service discovery** — Use readable names instead of IP addresses or auto-generated FQDNs

### Limitations

- Only resolves for resources inside the linked VNet
- No public internet visibility

## Adding More Records

To add additional DNS records (e.g., for other internal services), edit `deploy.parameters.sh` and define:

```bash
export YOUR_SERVICE_RECORD_NAME="service-name"
export YOUR_SERVICE_PRIVATE_IP="10.40.x.x"
```

Then modify `deploy.sh` to add a similar block:

```bash
if [ -n "${YOUR_SERVICE_RECORD_NAME}" ] && [ -n "${YOUR_SERVICE_PRIVATE_IP}" ]; then
    echo -e "${YELLOW}Adding service DNS record...${NC}"
    az network private-dns record-set a create ... # etc.
fi
```

Or add records manually using the Azure CLI:

```bash
az network private-dns record-set a create \
    --resource-group "rg-myapp-network" \
    --zone-name "internal.contoso.com" \
    --name "my-service"

az network private-dns record-set a add-record \
    --resource-group "rg-myapp-network" \
    --zone-name "internal.contoso.com" \
    --record-set-name "my-service" \
    --ipv4-address "10.40.x.x"
```

## Idempotency

The script is idempotent. Re-running it updates DNS records to match the current parameter values.

## Testing

After deploying the DNS zone and adding records, test name resolution from a client inside the VNet:

```bash
# From a VM in the ClientVM subnet (or another VNet resource)
nslookup ca-myapp-proxy.internal.contoso.com

# Or on Linux/macOS
dig ca-myapp-proxy.internal.contoso.com
```

## Troubleshooting

- **DNS record not resolving** — Ensure the VNet is linked to the DNS zone and the record exists.
- **Link not appearing** — VNet links can take a few seconds to propagate.
- **Record updates not reflecting** — DNS caches may require a refresh; consider the TTL and client cache behavior.
