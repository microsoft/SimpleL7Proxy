# Run SimpleL7Proxy

Run the proxy in Azure or locally, connect one backend, and verify that traffic reaches it.

## 1. Choose Where to Run

**Use the local path for development and the Container Apps path for an Azure deployment.**

<table>
<tr>
<td width="50%" valign="top">

### [Run in Azure Container Apps](container-apps.md)

Deploy the starter package directly to Azure.

![Azure Container Apps deployment](container-apps.png)

</td>
<td width="50%" valign="top">

### [Run Locally](local.md)

Use .NET 10 or Docker.

</td>
</tr>
</table>


## 2. Connect a Backend

The proxy is now installed and needs to be configured.  The quickest way is to navigate to the CompanionApp and then configure it.  The URL can be found in the **Azure portal** but was also provided after the completion of the **deploy.sh** script.

<table><tr><td width="50%">

1. Nagivate to the configuration screen and selects hosts to configure it.

![alt text](configure.png)

</td></tr>
<tr><td width="30%">

2. Select add host to add a new backend host.

![alt text](hosts.png)
</td></tr>
<tr><td width="30%">

3. Enter a hostname, endpoint, mode=direct and then configure either token or key based access to the host 

![alt text](host-access.png)

</td></tr>
<tr><td width="30%">

1. Configure model and routing to tell the proxy which endpoints support the different models. The host you added in the previous step will appear in the list.

![alt text](routing.png)

</td></tr>
<tr><td>

5. Map your endpoint to a model
 
![alt text](model-map.png)

</td></tr>
<tr><td>

6. Enter the costs for your model. Make sure the model name matches the model name for your host.
 
![alt text](costs.png)

</td></tr>

<tr><td>

7. Make sure to save your changes by clicking on **Update**.
   
![alt text](update.png)
 
</table>


## 4. Try it out

**Use CompanionApp for an interactive request and telemetry view.**


Navigate to **Explore / Ingestigator** and then try out a request by selecting a model and a prompt.  If you configured client authentication, you will need to enter the matching authentication on the server tab. 


You can view live telemetry from the tokenomics reports page.

<table>
<tr><td width="60%" align="center">

![alt text](tokenomics-util.png)
</td></tr>
<tr><td width="60%" align="center">

![alt text](tokenomics-policy.png)
</td></tr>
<tr><td width="60%" align="center">

![alt text](tokenomics-routing.png)
</td></tr>
<tr><td width="60%" align="center">

![alt text](tokenomics-errors.png)
</td></tr>
<tr><td width="60%" align="center">

![alt text](tokenomics-governance.png)
</td></tr>
<tr><td width="60%" align="center">

![alt text](tokenomics-abuse.png)
</td></tr>

<tr>
<td width="33%" align="center"><a href="chat-requests.png"><img src="chat-requests.png" alt="Event Hub request view" width="100%"></a></td>
<td width="33%" align="center"><a href="chat-monitor.png"><img src="chat-monitor.png" alt="Event Hub monitor view" width="100%"></a></td>
<td width="33%" align="center"><a href="chat-insights.png"><img src="chat-insights.png" alt="Event Hub insights view" width="100%"></a></td>
</tr>
<tr>
<td width="33%" align="center"><a href="chat-enqueue.png"><img src="chat-enqueue.png" alt="Event Hub enqueue view" width="100%"></a></td>
<td width="33%" align="center"><a href="chat-attempt.png"><img src="chat-attempt.png" alt="Event Hub attempt view" width="100%"></a></td>
<td width="33%" align="center"><a href="chat-final.png"><img src="chat-final.png" alt="Event Hub final response view" width="100%"></a></td>
</tr>
</table>

## Verification Checklist

- [ ] `/liveness` returns `200 OK`.
- [ ] `/readiness` returns `200 OK`.
- [ ] A test request returns the expected backend response.
- [ ] `BackendHost` identifies the selected backend on a successful proxied response.
- [ ] CompanionApp or `eventslog.json` shows the request when telemetry is configured.

## Next Steps

| Task | Document |
|------|----------|
| Run from source or Docker | [Run Locally](local.md) |
| Configure Azure App Configuration | [Configure Azure App Configuration](../how-to/configure-app-configuration.md) |
| Deploy to Azure Container Apps | [Deploy to Azure Container Apps](../how-to/deploy-container-apps.md) |
| Test without a model endpoint | [LLM Simulator](../how-to/llm-simulator.md) |
| Review runtime settings | [Environment Variables](../reference/environment-variables.md) |
| Contribute to the project | [Development and Contributing](../contributing/README.md) |
| Diagnose startup or request failures | [Troubleshooting](../troubleshooting/README.md) |

For exact health behavior and a focused verification sequence, see [Verify SimpleL7Proxy](verify.md).