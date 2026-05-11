# Deployment Prerequisites

This document consolidates the prerequisites for the current SimpleL7Proxy deployment flows. It is intended to be the starting point before running any of the setup or deployment scripts in this repository.

The requirements differ depending on whether you are doing local development or pure Azure deployment:

- For local development, you typically need `.NET 10 SDK` and may also need Docker if you want local container builds.
- For pure deployment, those local development tools are not required when you use Azure CLI, AZD, and remote ACR builds.

## Quick Validation

Before starting any deployment, verify your environment:

```bash
cd deployment/Prereq
./validate.sh
```

This script checks for all required and optional tools, Azure CLI authentication, and subscription access. Fix any failures before proceeding with deployment.

## Supported Deployment Paths

The repository currently supports these main deployment paths:

1. AZD-driven Azure Container Apps deployment via `.azure/setup.sh` and `.azure/deploy.sh`
2. Direct sidecar Container App deployment via `deployment/proxy-with-sidecar/setup.sh` and `deploy.sh`
3. Add-on resource provisioning via `deployment/AppConfiguration/deploy.sh` and `deployment/BlobStorage/deploy.sh`
4. APIM policy deployment and OAuth wiring via `APIM-Policy/` and `scripts/`
5. Sample backend setup for local validation via `test/nullserver/Python` or Python's built-in HTTP server

## Common Prerequisites

These are the baseline requirements for almost every deployment workflow.

### 1. Source and runtime tools

- Git
- Bash-compatible shell for `.sh` scripts
- Python 3 for the included sample backend and simple local mock servers

Notes:

- `.NET 10 SDK` is not required for the deployment path documented here because images are built remotely in Azure Container Registry rather than with local `dotnet` or Docker builds.
- install `.NET 10 SDK` if you plan to run, debug, or develop SimpleL7Proxy locally
- install Docker only if you plan to do local container builds or local container-based debugging

## Development Versus Deployment

Use this distinction when deciding what to install on a machine.

### Local Development Machine

Use this profile if the machine will be used for source changes, local debugging, or local backend/proxy testing.

Typical requirements:

- Git
- `.NET 10 SDK`
- Azure CLI
- Azure Developer CLI if you also run the AZD workflow
- Bash-compatible shell
- Python 3 for the included sample backend
- Docker only if you want local container builds or local container testing

### Deployment-Only Machine

Use this profile if the machine will only provision Azure resources, run deployment scripts, seed configuration, and configure APIM.

Typical requirements:

- Git
- Azure CLI
- Azure Developer CLI for the `.azure/*` workflow
- `jq` for App Configuration and Blob Storage helper scripts
- Bash-compatible shell

Not required for this deployment-only profile:

- `.NET 10 SDK`
- Docker, when remote ACR builds are used

On Windows, use one of these:

- WSL
- Git Bash
- another shell that supports standard Bash script behavior

### 2. Azure tooling

- Azure CLI (`az`)
- Azure Developer CLI (`azd`) for the `.azure/*` workflow
- `jq` for scripts that inspect Container App JSON output

### 3. Azure access

You need an Azure subscription and enough permissions to:

- create or update resource groups
- create Container Apps and Container Apps environments
- create or use Azure Container Registry
- create role assignments for managed identities when using the resource provisioning scripts

In practice, the deployment account usually needs a combination of:

- `Contributor` on the target resource group or subscription
- permission to create RBAC assignments, typically `User Access Administrator` or equivalent delegated rights

### 4. Backend information

Before deployment, have at least one backend host ready. The current scripts expect a host definition similar to:

```bash
host=https://your-api.example.net;mode=apim;path=/;probe=/health
```

If you do not have backend details yet, prepare placeholder values for the initial deployment and update them before testing real traffic.

## Optional but Common Prerequisites

### Docker

Docker is optional and is not required for the planned deployment flow documented here because image builds can be done with Azure Container Registry remote builds.

Install Docker if you want to:

- run local container builds
- use the sidecar image build scripts locally

For this deployment plan, prefer remote ACR builds with `az acr build` instead of local Docker builds.

Primary remote build helper:

- `.azure/deploy-container-to-registry.sh`

### Azure Portal access

Portal access is useful for:

- confirming resource creation
- reviewing Container App configuration
- validating App Configuration keys
- editing and validating APIM policies
- troubleshooting role assignments and managed identities

## Deployment Path Requirements

## AZD Workflow

Use this path for the main Azure Container Apps deployment flow.

Required:

- Azure CLI
- Azure Developer CLI
- Bash on Linux/macOS or PowerShell/Bash on Windows
- access to Azure Container Registry for remote builds via `az acr build`

Recommended:

- ability to run `azd provision --preview` before `azd provision`
- one of the predefined deployment scenarios under `.azure/scenarios`

Primary scripts:

- `.azure/setup.sh`
- `.azure/deploy.sh`

## Direct Sidecar Container App Workflow

Use this path when deploying with `deployment/proxy-with-sidecar`.

Required:

- Azure CLI
- Bash
- an Azure Container Registry name
- prepared image names or a remote image build strategy
- at least one backend host entry via `HOST1`

Primary files:

- `deployment/proxy-with-sidecar/deploy.parameters.example.sh`
- `deployment/proxy-with-sidecar/setup.sh`
- `deployment/proxy-with-sidecar/deploy.sh`

## Azure App Configuration Provisioning

Use this path when seeding proxy settings into Azure App Configuration.

Required:

- Azure CLI
- Bash
- `jq`
- an existing Container App to read environment values from
- permission to assign `App Configuration Data Reader` to the Container App managed identity

Primary files:

- `deployment/AppConfiguration/deploy.parameters.example.sh`
- `deployment/AppConfiguration/deploy.sh`

Notes:

- the script reads the live Container App configuration
- the script can create the App Configuration store if needed
- the script can update Container App environment variables to point at the App Configuration endpoint

## Blob Storage Provisioning

Use this path when the proxy needs blob-backed configuration or async storage support.

Required:

- Azure CLI
- Bash
- `jq`
- an existing Container App to receive RBAC access
- permission to assign `Storage Blob Data Contributor` or the configured storage role

Primary files:

- `deployment/BlobStorage/deploy.parameters.example.sh`
- `deployment/BlobStorage/deploy.sh`

## APIM Deployment And Integration

Use this path when SimpleL7Proxy will sit behind Azure API Management or when you want the APIM policy to provide retry, priority, throttling, and affinity behavior.

Required:

- Azure subscription with an existing APIM instance, or a plan to provision one separately
- Azure portal or APIM automation access to edit API policies
- Azure CLI for the auth setup scripts under `scripts/`
- Microsoft Entra permissions to create or manage app registrations and service principals when enabling OAuth flows

Recommended:

- at least one backend Azure OpenAI or compatible endpoint ready for the APIM policy's backend list
- a clear header contract for priority, affinity, and retry handling between client, APIM, and proxy

Primary files:

- `APIM-Policy/Priority-with-retry.xml`
- `APIM-Policy/Priority-with-retry-enhancedLog.xml`
- `APIM-Policy/readme.md`
- `scripts/ca2apimSetup.sh`
- `scripts/console2caSetup.sh`
- `scripts/enableContainerAppAuth.sh`

Notes:

- the APIM policy is not just an optional sample; it is the recommended routing companion when using SimpleL7Proxy behind APIM
- the auth scripts create and connect app registrations, service principals, managed identity access, and Container App Easy Auth settings
- plan for secret handling and rotation if you use the sample OAuth setup flow from `scripts/README.md`

## Sample Backend Setup

Use this path when you want a lightweight backend for local validation before connecting the proxy to APIM or a real model backend.

Required:

- Python 3
- a free local port such as `3000` or `5000`
- at least one proxy host variable such as `Host1=http://localhost:3000`

Primary files:

- `docs/DUMMY_BACKEND.md`
- `test/nullserver/Python/streamserver.py`

Recommended local flow:

1. start the sample backend on `localhost:3000`
2. point `Host1` at that backend
3. start the proxy locally or in a container
4. verify backend reachability with `curl` before testing through the proxy

Notes:

- the included null server is the fastest path because it is already in the repo
- Python's built-in `http.server` also works for static-file tests
- if you run the proxy in Docker while the backend stays on the host, use `host.docker.internal` instead of `localhost`

## Recommended Validation Before First Deployment

Run these checks before starting:

```bash
az version
az account show
azd version
jq --version
docker --version
```

Notes:

- `azd version` is only required for the AZD deployment path
- `dotnet --version` is only required if you explicitly plan to run or debug the proxy locally
- `docker --version` is only required if you explicitly choose local image builds
- `jq --version` is required for the App Configuration and Blob Storage scripts

If you are using remote ACR builds, add this check:

```bash
az acr check-health --name <your-acr-name>
```

## Minimum Information To Gather Up Front

Have these values ready before running the setup or interview flow:

- deployment path to use first
- Azure subscription
- Azure region
- target environment name such as `dev`, `test`, or `prod`
- resource group naming convention
- Container App name
- Container Apps environment name
- Azure Container Registry name or image source strategy
- confirmation that remote ACR build is the selected image build path
- backend host definitions
- whether APIM is in scope
- APIM instance name and target API if APIM is in scope
- whether OAuth/Easy Auth setup scripts are in scope
- whether App Configuration is in scope
- whether Blob Storage is in scope
- whether VNET/private networking is in scope

## Security Guidance

- Prefer managed identity for Azure-hosted workloads.
- Do not store secrets in committed parameter files.
- Keep environment-specific parameter files such as `deploy.parameters.sh` out of source control.
- Scope RBAC assignments to the smallest resource scope that works.

## Related Documentation

- `README.md`
- `.azure/README.md`
- `APIM-Policy/readme.md`
- `scripts/README.md`
- `docs/CONTAINER_DEPLOYMENT.md`
- `docs/AZURE_APP_CONFIGURATION.md`
- `docs/DUMMY_BACKEND.md`
- `docs/SIDECAR_DEPLOYMENT.md`
