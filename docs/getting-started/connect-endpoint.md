# Connect to an LLM Endpoint ( Direct Host )

Point the proxy directly at an Azure OpenAI / Azure AI Foundry deployment — no gateway in between. This is the lowest-cost option when you don't need APIM's policy engine on top.

<details>
<summary><strong>Connect with an API Key</strong></summary>

```bash
export AZURE_OPENAI_API_KEY="<KEY>"
export Host_endpoint1="host=<URL>;path=/;mode=direct;api_key=${AZURE_OPENAI_API_KEY}"

```

If correctly configured, you will see something like this:
![alt text](S7P-port-key.png)

> [!NOTE]
> Many organizations disable key based auth, so you may need to use Managed Identity.

</details>


<details>
<summary><strong>Connect with Managed Identity</strong></summary>

In this scenario, you want the proxy to use its managed identity to authorize to the endpoint.  You will need to grant the apropriate identity.  In the case of `OpenAI`, use `Cognitive Services OpenAI User` and assing it to your account ( or VM ).

```bash
export Host1="host=<URL>;path=/;usemi=true; audience=https://cognitiveservices.azure.com"
```
If correctly configured, you will see something like this:
![alt text](S7P-port-mi.png)

> [!NOTE]
> We are using `https://cognitiveservices.azure.com` for OpenAI, update if your audience is different
 
</details>

See [→ AI Foundry Integration](../AI_FOUNDRY_INTEGRATION.md) for the full configuration guide.

---

[← Back to Where are your LLM models?](02-get-it-running.md#step-2-where-are-your-llm-models)
