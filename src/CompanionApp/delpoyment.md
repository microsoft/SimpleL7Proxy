# Deploy SimpleL7Proxy

## Deployment prerequisites

### Before you begin

Make sure you have:

- **.NET 10 SDK** and a local checkout of the **SimpleL7Proxy repository** to run CompanionApp. Follow the [shared setup](readme.md#set-up-companionapp).
- **Azure portal access** to the target tenant and subscription.
- **Azure CLI and Bash** to publish images or use the standalone workflow. Follow the [Azure sign-in steps](readme.md#sign-in-for-azure-workflows).
- **Docker**, installed and running, if you build images locally. Remote builds use Azure Container Registry (ACR).
- **A backend endpoint** for Host1, with any required credentials. The deployed proxy must be able to reach it.

### Check your Azure permissions

For the generated portal deployment, the deploying account needs the following roles. Check the account used in the portal and Azure CLI.

| Role | Where to assign it | What it allows |
| --- | --- | --- |
| [Contributor](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged#contributor) | Target subscription. | Create resource groups, deploy ARM templates, and provision infrastructure. |
| [Role Based Access Control Administrator](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged#role-based-access-control-administrator) | Each affected resource or resource group. Subscription scope also covers new groups. | Assign access to the deployed managed identities. |
| [App Configuration Data Owner](https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-enable-rbac#data-plane-access) | App Configuration resource group after bootstrap, or the target subscription. | Write configuration values during the application stage. |

Resource-group-only access does not cover the subscription-scoped bootstrap. Contributor alone cannot assign roles. Grant App Configuration Data Owner before starting the application stage.

If you already have one of these combinations, it covers infrastructure deployment and role assignment:

- [Owner](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged#owner) at the target subscription.
- Contributor plus [User Access Administrator](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged#user-access-administrator) at the applicable scopes.

> [!IMPORTANT]
> **App Configuration Data Owner is still required with either combination.** The generated store uses pass-through authentication with access keys disabled.

These roles are for the deployer. The portal template assigns the proxy and RequestAPI managed identities their own resource-scoped runtime roles.

> [!NOTE]
> Check **Subscriptions > your subscription > Access control (IAM) > View my access** for the deploying account and activate any eligible roles in Privileged Identity Management (PIM) before starting. Role-assignment conditions must permit the roles and managed identities used by the selected deployment options. For async deployments, access must also cover the existing Service Bus and Cosmos DB resource groups.

## Prepare the deployment

**Deployment Setup prepares files for the proxy stack. It does not deploy Azure resources or host CompanionApp itself.** Use this workflow when creating a deployment, not when editing an existing store's settings.

Deployment values are entered in **Config > Deployment Setup** (`/admin/deployment`). You do not need to set CompanionApp's local `AppConfigurationEndpoint` to prepare these files; that setting is used by the separate [configuration editor](configuration.md#configuration-prerequisites).

Work through the tabs:

1. **Mode:** select private networking and asynchronous processing only when needed.
2. **Resources:** set the Azure region and resource groups. Check every generated resource name; the shared five-character suffix reduces collisions but does not check Azure name availability.
3. **Images:** confirm the registry and build method. Remote builds use ACR; local builds use Docker.
4. **Container Apps:** set the application, environment, image, sizing, and health-probe choices. Replace **Host1** with a backend URL and authentication settings reachable from the deployed proxy, not the sample address.
5. **App Configuration:** set the store name and label for the new proxy. These deployment values are separate from the local endpoint and label used by **Proxy Configuration**.
6. Complete the **Networking** and **Async services** tabs when enabled. Open **Review**, resolve every validation error, and inspect the resource summary before choosing a deployment method.

Keep the same form values for all downloads. Navigating away or reloading can discard the form and generate different default names. Downloads go to the browser; they are not written into the server's checkout automatically.

## Interactive via Portal

1. Download **01-bootstrap.json**. Open the [Azure portal Custom deployment](https://portal.azure.com/#create/Microsoft.Template), select **Build your own template in the editor > Load file**, and deploy it to the intended subscription and location. Wait for the resource groups and ACR to finish creating.
2. Download **02-publish-images.sh**. From the repository root, run it with the same subscription ID. Replace the script path and ID in this command:

```bash
bash /path/to/02-publish-images.sh YOUR_SUBSCRIPTION_ID
```

3. Continue only after the script reports **Image tags verified**. Download **03-application.json** and submit it as a new portal Custom deployment in the same subscription. It reuses the groups and registry and deploys the remaining selected resources.

> [!NOTE]
> The form does not check your Azure permissions, quota, resource-name availability, or existing dependencies. Resolve any Azure deployment validation errors before continuing to the next stage. Keep downloaded templates private if Host1 contains credentials.

## Standalone

1. Select **Standalone**, then **Download parameters**. **Preview file** shows the shell parameters before download.
2. Place the downloaded `deploy.parameters.sh` in the repository's `deployment` directory. Preserve any existing parameter file before replacing it.
3. Confirm your Azure CLI tenant and subscription. From the repository root, open the deployment menu and run its prerequisites check first:

```bash
cd deployment
bash deploy.sh
```

Follow [Running through the deployment](../../deployment/README.md#running-through-the-deployment) for the menu order, prerequisites, parameter definitions, and side effects. The standalone Container Apps step needs a preconfigured VNet-attached environment when private networking is enabled.

> [!WARNING]
> Async deployments depend on existing Service Bus namespaces/queues and Cosmos DB accounts/databases/containers. The portal application template creates RequestAPI infrastructure only; publish its code separately before using async requests. See the [deployment guide](../../deployment/README.md).

## Verify the deployment

- [ ] Azure reports successful deployments, and the proxy's container revision is running with the intended images.
- [ ] The proxy readiness endpoint returns `200 OK`; a test request reaches the configured backend. Follow [Verify SimpleL7Proxy](../../docs/getting-started/verify.md) for the exact health and request checks.

After deployment, use [Update Azure App Configuration](configuration.md#configuration-prerequisites) to connect to the new store and label, or [Test HTTP and LLM endpoints](testing.md#request-testing-prerequisites) to connect to the proxy and inspect traffic.

## Troubleshoot deployment

| Symptom | Check |
| --- | --- |
| Review shows a Host1 validation error | Replace the sample backend with a valid HTTP or HTTPS host descriptor in **Container Apps**, then return to Review. |
| Azure reports a name collision or missing image | Change the conflicting resource name before regenerating the deployment files; publish and verify the matching image tags before the application stage. |

For hosting CompanionApp itself on an existing Azure App Service, the separate scripts are [make-zip.sh](make-zip.sh), [upload_zip.sh](upload_zip.sh), and [update_settings.sh](update_settings.sh). Deployment Setup does not run them. Review their help and protect the app's admin access before publishing it.