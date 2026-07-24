# Run in Azure Container Apps

Before the proxy can accept traffic, it needs a port to listen on. In Azure Container Apps you can set this by deploying with App Configuration, which pushes the `Port` setting to the container for you, or by setting it as a manually defined environment variable on the Container App.

## Prerequisites

Deploying to ACA needs three things in place first:

* **Azure Container Registry (ACR)** — holds the proxy's container image.
* **Azure Container Apps environment** — where the proxy runs.
* **App Configuration** — stores runtime settings such as `Port` so they aren't hardcoded.

## Deploy with the Setup Script

The included setup script creates all three for you, so there's nothing to provision by hand first.

```bash
cd deployment
cp deploy.parameters.example.sh deploy.parameters.sh
vi deploy.parameters.sh        # set LOCATION, resource group names, ACR_NAME, etc.
./deploy.sh                    # interactive menu
```

From the menu, the relevant steps are:

```
1)  Prerequisites              (Prereq/validate.sh)
3)  Validate/Create ACR        (ContainerImage/validate-acr.sh)
5)  Azure Container Apps       (proxy/deploy.sh)
7)  App Configuration          (AppConfiguration/deploy.sh)
```

See [deployment/README.md](../../deployment/README.md) for the full parameter reference and day-2 operations.

---

[← Back to Choose Your Setup](02-get-it-running.md#step-1-choose-your-setup)
