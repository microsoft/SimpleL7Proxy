# Container Image Build

Builds the SimpleL7Proxy container image and pushes it to Azure Container Registry (ACR).

**Recommended:** Use remote ACR builds (no Docker required). Local builds with Docker are also supported for faster feedback during development.

This folder follows the same deployment convention as other packages:

1. Copy `../deploy.parameters.example.sh` to `../deploy.parameters.sh` (shared by all deployment scripts)
2. Update values
3. Run `./deploy.sh`

## Prerequisites

### For Remote Builds (Recommended)

| Requirement | Details |
|---|---|
| Azure CLI | `az` installed and authenticated |
| ACR | Container Registry created in Azure |

### For Local Builds (Optional)

| Requirement | Details |
|---|---|
| Docker | Docker Desktop or Docker daemon installed and running |
| ACR login | `az acr login --name myregistry` |
| Azure CLI | `az` installed |

## Quick Start

```bash
cd deployment

# 1. Create the shared parameters file (used by all deploy/build scripts)
cp deploy.parameters.example.sh deploy.parameters.sh

# 2. Edit deploy.parameters.sh (defaults use remote ACR build)
#    - Set ACR_NAME to your registry
#    - Set PROXY_IMAGE_NAME (image repo name)
#    - Leave BUILD_METHOD as "remote" (no Docker needed)

# 3. Run
cd ContainerImage
./deploy.sh
```

The script will:
- Extract the version from `src/SimpleL7Proxy/Constants.cs`
- Submit build job to ACR
- Image will be available as: `myregistry.azurecr.io/simple-l7-proxy:vX.Y.Z`
- Ready for ACA deployment

## Parameters

All parameters are set in the shared `../deploy.parameters.sh`.

| Parameter | Description |
|---|---|
| `ACR_NAME` | Azure Container Registry name (without `.azurecr.io`) |
| `PROXY_IMAGE_NAME` | Image repository name (e.g., `simple-l7-proxy`) |
| `BUILD_METHOD` | `remote` (ACR builds, no Docker) or `local` (Docker on your machine) |
| `DOCKERFILE_PATH` | Path to Dockerfile relative to `src/` |

## Build Methods

### Remote Build (Recommended - No Docker Required)

When `BUILD_METHOD=remote`:

```bash
./deploy.sh
```

**What happens:**
1. Extracts version from Constants.cs
2. Submits build job to Azure Container Registry
3. ACR builds and pushes image
4. Shows full image URI

**Pros:**
- ✅ No Docker installation needed
- ✅ Ideal for deployment-only machines
- ✅ Builds use ACR's infrastructure
- ✅ Works on any OS with Azure CLI

**Cons:**
- Slower feedback (build runs in Azure)
- Requires ACR resource and Azure subscription

**Time:** ~3-5 minutes

### Local Build (Optional - Requires Docker)

When `BUILD_METHOD=local`:

```bash
./deploy.sh
```

**What happens:**
1. Extracts version from Constants.cs
2. Authenticates to ACR
3. Runs `docker build` locally
4. Runs `docker push` to ACR
5. Shows full image URI

**Pros:**
- Faster feedback loop
- Build runs on your machine
- Useful for development/testing

**Cons:**
- Requires Docker installation
- Docker must be running
- May be slower on resource-constrained machines

**Time:** ~5-10 minutes depending on dependencies

## Automatic Version Detection

The script automatically:
1. Reads `src/SimpleL7Proxy/Constants.cs`
2. Extracts `VERSION = "X.Y.Z"`
3. Adds `v` prefix if missing: `vX.Y.Z`
4. Tags image as: `myregistry.azurecr.io/simple-l7-proxy:vX.Y.Z`

Update the version in Constants.cs to create a new build with a new tag.

### Check the Current Version

To see what version will be used for the next build:

```bash
./get-version.sh
# Output: v1.2.3
```

This is useful before running `deploy.sh` to confirm the version or to communicate to the ACA deployment step what image URI to use.

## After Building

Once the build completes, use the image URI in ACA deployment:

```bash
# Get the version from Constants.cs
VERSION=$(cd ContainerImage && ./get-version.sh)

# Go to ACA folder
cd ../ACA
# The shared ../deploy.parameters.sh already defines PROXY_IMAGE
# (built from ACR_NAME + PROXY_IMAGE_NAME + version), so just run:
./deploy.sh
```

## Troubleshooting

### Version not extracted
Check that `src/SimpleL7Proxy/Constants.cs` exists and contains:
```csharp
public const string VERSION = "1.0.0";
```

### Remote build fails
- Verify ACR exists: `az acr show --name myregistry`
- Check subscription: `az account show`
- Review build logs: `az acr task logs --registry myregistry`

### Docker daemon is not running (Local Build Only)
Start Docker Desktop (macOS/Windows) or start the Docker service (Linux).

### ACR login failed (Local Build Only)
Ensure you're authenticated:
```bash
az acr login --name myregistry
```

## Manual Build (Alternative)

If you prefer to build manually without the script:

```bash
# Remote build (no Docker needed)
az acr build --registry myregistry \
    --image simple-l7-proxy:v1.0.0 \
    --file src/SimpleL7Proxy/Dockerfile src/

# Local build (requires Docker)
docker build -t myregistry.azurecr.io/simple-l7-proxy:v1.0.0 \
    -f src/SimpleL7Proxy/Dockerfile src/
docker push myregistry.azurecr.io/simple-l7-proxy:v1.0.0
```
