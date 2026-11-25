# Run SimpleL7Proxy

Start the proxy, connect a backend, then confirm a request reaches it.

## TL;DR

1. **Run:** [deploy to Azure Container Apps](container-apps.md).
2. **Connect:** use CompanionApp to [add a host](companionapp-add-host.md) and [assign models and APIs](companionapp-model-routing.md).
3. **Verify:** [send a request in Investigator](companionapp-investigator.md) and [check proxy health](verify.md).

## 1. Run

**Deploy the proxy and CompanionApp to Azure Container Apps.**

Follow [Deploy to Azure Container Apps](container-apps.md), selecting CompanionApp for deployment, then open its URL from the deployment output.

> [!WARNING]
> The prebuilt deployment starts with the proxy and CompanionApp unlocked. Unauthorized users could send requests or change proxy configuration. Running them without access controls is not recommended. CompanionApp reminds you in each browser session while its authentication is disabled; follow its setup instructions and [secure the proxy](../how-to/configure-security.md).

## 2. Connect

**After the proxy is deployed, configure a backend in CompanionApp.**

1. Open CompanionApp and connect it to the proxy's App Configuration store and label. See [configuration prerequisites](../../src/CompanionApp/configuration.md#configuration-prerequisites).
2. [Add a host](companionapp-add-host.md) for your backend.
3. [Assign models and APIs](companionapp-model-routing.md) to the host and save your changes.

To configure a backend with environment variables instead, follow [Connect an endpoint](connect-endpoint.md), [Connect APIM](connect-apim.md), or [Use the LLM Simulator](connect-llm-simulator.md). Restart the proxy with the selected host configuration.

## 3. Verify

**Check proxy health, then send a request through Investigator.**

```bash
PROXY_URL="https://<proxy-url>"
curl -i "$PROXY_URL/liveness"
curl -i "$PROXY_URL/readiness"
```

- Replace `<proxy-url>` with the proxy URL from the deployment output.
- `/liveness` returns `200 OK` when the proxy is running.
- `/readiness` returns `200 OK` when the proxy is ready to accept traffic.
- [Send a request with Investigator](companionapp-investigator.md), then use [Verify SimpleL7Proxy](verify.md) to check the response and selected backend.

> [!TIP]
> A `503` from `/readiness` can indicate incomplete startup, no eligible backend, or an event or blob queue backlog. Check the [health endpoints](../reference/health-endpoints.md) and [verification guide](verify.md).

## Next steps

- [Explore the CompanionApp visual guides](companionapp-visual-guide.md).
- [Configure runtime settings](../reference/environment-variables.md).
- [Troubleshoot a failure](../troubleshooting/README.md).
- [Explore proofs of concept](../pocs/README.md).
