# Create a Custom Deployment Package

CompanionApp prepares a deployment package locally. Downloading it does not deploy resources.

1. [Set up and start CompanionApp](../../src/CompanionApp/readme.md#set-up-companionapp).
2. Open **Deployment Setup** (`/admin/deployment`) and complete the setup tabs.
3. Review the topology, open **Deployment**, and download the ZIP.

Extract the ZIP and open its `README.md`. The package includes Bicep templates, deployment parameters, and a `deploy.sh` script.

> [!NOTE]
> Generate a new package after changing names, topology, images, or features. Use one extracted package for every operation in the same deployment.
