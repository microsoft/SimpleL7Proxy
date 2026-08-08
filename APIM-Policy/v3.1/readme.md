# Configure the APIM v3.1 Policy

Configure Azure API Management to route LLM requests by model, priority, endpoint availability, and authentication mode.

> [!IMPORTANT]
> Create the policy fragment before applying the API policy. `Priority-with-retry.xml` references a fragment with the exact ID `endpoint_selection_frag_30`.

## Overview

<img width="1308" height="334" alt="image" src="https://github.com/user-attachments/assets/60b20f0c-cee1-44b7-8f6a-b97d84f590bf" />

This policy can configure an APIM instance to:

- Route each model to its own set of endpoints.
- Try endpoints in priority order, load balance endpoints in the same group, and fail over when needed.
- Restrict endpoints to specific request priorities.
- Set endpoint-call attempts, throttling behavior, and whether a request can be requeued.
- Authenticate to endpoints with Managed Identity or an API key.
- Control streaming and token processing and return routing diagnostics.

`endpoint_selection_frag_30.xml` defines these settings, and `Priority-with-retry.xml` applies them to each request. Configure the marked sections in the fragment, set `DefaultModel` in the API policy, deploy the fragment first, and then deploy and verify the API policy.

## Prerequisites

- An existing Azure API Management instance with a target API.
- One or more reachable LLM endpoints.
- Permission to manage APIM policy fragments and API policies.
- For Managed Identity authentication, the APIM system-assigned identity must have data-plane access to every endpoint configured with `auth = "MI"`.
- For API-key authentication, store keys in APIM named values or Key Vault-backed named values. Do not commit literal keys to this file.

## Configure

Edit only the four sections marked `edit me` near the top of `endpoint_selection_frag_30.xml`. Leave the runtime variables below them unchanged.

### 1. Set Request Headers

These variables map the request headers used for model selection, priority, affinity, and retry tracking. SimpleL7Proxy sends them automatically; standalone clients can omit them to use policy defaults, but must send `x-LLMModel` to override `DefaultModel`.

```xml
<set-variable name="priorityHeaderName" value="x-S7PPriority" />
<set-variable name="PolicyCycleCounterHeaderName" value="x-PolicyCycleCounter" />
<set-variable name="AffinityHeaderName" value="x-backend-affinity" />
<set-variable name="modelHeaderName" value="x-LLMModel" />
```

### 2. Add Models and Endpoints

Each top-level key in `backendCatalog` is a model; each nested entry is an endpoint. Replace every sample URL and keep a `DEFAULT` model block for unmatched model names. The example below prefers PTU capacity and uses PAYGO as the fallback for `gpt-4o` and uses managed identity for authentication.

See [Endpoint Fields and Defaults](#endpoint-fields-and-defaults) for all endpoint options.
```csharp
["gpt-4o"] = new JObject {
  ["PTU"] = new JObject {
    ["url"] = "https://<ptu-resource>.openai.azure.com/",
    ["priorityGroup"] = 1,
    ["auth"] = "MI"
  },
  ["PAYGO"] = new JObject {
    ["url"] = "https://<paygo-resource>.openai.azure.com/",
    ["priorityGroup"] = 2 ,   // Override the default to make PAYGO the second priority group.
    ["auth"] = "MI"
  }
}
```

### 3. Set Authentication

Set `auth` to `MI` for Managed Identity, an APIM named-value expression for API-key authentication, or an empty string for no endpoint authentication.

```csharp
["auth"] = "MI"
// or
["auth"] = "{{endpoint-api-key}}"
```

For Managed Identity, grant the APIM system-assigned identity data-plane access to the LLM endpoint and map the model to its token audience:

```csharp
["gpt-4o"] = "https://cognitiveservices.azure.com",
["DEFAULT"] = "https://cognitiveservices.azure.com"
```

### 4. Set Retry and Requeue Rules

Each request priority can have its own retry and requeue behavior. For example, use the following configuration to try high-priority (`priority=1`) requests five times, medium-priority (`priority=2`) requests three times, and low-priority (`priority=3`) requests once before requeuing them:

```csharp
    <set-variable name="priorityCfg" value="@{
        return new JObject {
            ["1"] = new JObject { ["retryCount"] = 5, ["requeue"] = false },
            ["2"] = new JObject { ["retryCount"] = 3, ["requeue"] = false },
            ["3"] = new JObject { ["retryCount"] = 1, ["requeue"] = true }
        };
    }" />
```

### 5. Set the Default Model

In `Priority-with-retry.xml`, set `DefaultModel` to a key from `backendCatalog`. Keep the fragment ID unchanged unless you deploy the fragment under a different ID.

```xml
<set-variable name="DefaultModel" value="gpt-4o" />
<include-fragment fragment-id="endpoint_selection_frag_30" />
```

## Deploy

Deploy the fragment before the API policy. Use the portal path below, or expand the Azure CLI or Bicep alternative.

### Azure Portal

1. Open **Policy fragments**, create `endpoint_selection_frag_30`, paste the complete `endpoint_selection_frag_30.xml` file, and save it.
2. Open **APIs** > **target API** > **All operations**, open the policy code editor, paste the complete `Priority-with-retry.xml` file, and save it.

<details>
<summary>Azure CLI</summary>

Run these commands from the repository root. The fragment must be deployed first.

```bash
resourceGroup="<resource-group>"
apimServiceName="<apim-name>"
apiId="<api-id>"

az apim policy fragment create \
  --resource-group "$resourceGroup" \
  --service-name "$apimServiceName" \
  --fragment-id endpoint_selection_frag_30 \
  --xml-file APIM-Policy/v3.1/endpoint_selection_frag_30.xml

az apim api policy create \
  --resource-group "$resourceGroup" \
  --service-name "$apimServiceName" \
  --api-id "$apiId" \
  --xml-file APIM-Policy/v3.1/Priority-with-retry.xml
```

Verify both resources:

```bash
az apim policy fragment show \
  --resource-group "$resourceGroup" \
  --service-name "$apimServiceName" \
  --fragment-id endpoint_selection_frag_30

az apim api policy show \
  --resource-group "$resourceGroup" \
  --service-name "$apimServiceName" \
  --api-id "$apiId"
```

</details>

<details>
<summary>Bicep</summary>

Place this Bicep file beside the two XML files. `loadTextContent()` resolves paths relative to the Bicep file.

```bicep
param apimName string
param apiId string

resource apim 'Microsoft.ApiManagement/service@2024-05-01' existing = {
  name: apimName
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' existing = {
  parent: apim
  name: apiId
}

resource endpointFragment 'Microsoft.ApiManagement/service/policyFragments@2024-05-01' = {
  parent: apim
  name: 'endpoint_selection_frag_30'
  properties: {
    description: 'SimpleL7Proxy endpoint selection for APIM policy v3.1'
    format: 'rawxml'
    value: loadTextContent('endpoint_selection_frag_30.xml')
  }
}

resource retryPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('Priority-with-retry.xml')
  }
  dependsOn: [
    endpointFragment
  ]
}
```

Deploy it at resource-group scope:

```bash
az deployment group create \
  --resource-group "$resourceGroup" \
  --template-file APIM-Policy/v3.1/apim-policy.bicep \
  --parameters apimName="$apimServiceName" apiId="$apiId"
```

</details>

## Verify the Policy

Send a request through the APIM API. Replace `<api-route>` with the complete route exposed by the target API. Remove the subscription-key header if the API does not require one.

```bash
curl -i "https://<apim-name>.azure-api.net/<api-route>" \
  -H "Content-Type: application/json" \
  -H "Ocp-Apim-Subscription-Key: <subscription-key>" \
  -H "x-LLMModel: gpt-4o" \
  -H "x-S7PPriority: 1" \
  -H "S7PDEBUG: true" \
  --data '{"model":"gpt-4o","messages":[{"role":"user","content":"hello"}],"stream":true}'
```

Check these response signals:

- [ ] The response is successful or contains the expected endpoint error body.
- [ ] `x-Backend-Label` and `x-Backend-Attempts` identify the selected endpoint and call count.
- [ ] `TOKENPROCESSOR` matches the selected endpoint's `tokenProcessor`.
- [ ] `x-backend-affinity`, `x-PolicyCycleCounter`, and `backendLog` show affinity, policy cycles, endpoint selection, and retries.

For a terminal `429`, check `S7PREQUEUE` for the requeue decision and `retry-after-ms` for the recommended delay.

## Troubleshooting

| Symptom | What to check |
| :--- | :--- |
| Fragment cannot be found | Deploy the fragment first and confirm its ID is exactly `endpoint_selection_frag_30`. |
| Endpoint returns `401` or `403` | Verify `auth`, `authResourceByModel`, the APIM identity role assignment, and the named value. |
| Wrong model or endpoint is selected | Compare `x-LLMModel` with `backendCatalog` and `DefaultModel`; inspect `x-Backend-Label` and `backendLog`. Model matching is case-insensitive. |
| No endpoint accepts the request | Compare `x-S7PPriority` with `acceptablePriorities`; inspect `backendLog` and `retry-after-ms` for throttling. |
| Token usage is parsed incorrectly | Compare the response format with `tokenProcessor` and inspect the `TOKENPROCESSOR` response header. |
| Streaming waits for the full response | Set `bufferResponse` to `false` for streaming endpoints. |

## Reference

### Request Header Defaults

Request headers carry the model, priority, affinity, and retry-cycle context used to route each call. The policy uses priority `3` when the priority header is missing, invalid, or outside the supported range.

<details>
<summary>Show request header defaults</summary>

| Purpose | Header or value |
| :--- | :--- |
| Model | `x-LLMModel` |
| Priority | `x-S7PPriority` |
| Missing or invalid priority | `3` |
| Affinity | `x-backend-affinity` |
| Policy cycle | `x-PolicyCycleCounter` |

</details>

### Endpoint Fields and Defaults

Endpoint fields determine which LLM endpoint receives a request and how APIM calls it. Only `url` has no usable default; omitted fields use the values below.

| Field | Purpose | Default |
| :--- | :--- | :--- |
| `url` | LLM endpoint base URL. | Required |
| `path` | Path appended to `url`. | `openai` |
| `priorityGroup` | Selection order; lower groups are selected first. | `1` |
| `acceptablePriorities` | Request priorities served by the endpoint. | `1`, `2`, and `3` |
| `timeout` | Endpoint timeout in seconds. | `10` |
| `auth` | `MI`, an API key, or an empty value for no authentication. | `MI` |
| `tokenProcessor` | Parses token usage from the response. | `DefaultStream` |
| `limitConcurrency` | Limits concurrent calls: `off`, `low`, `medium`, or `high`. | `off` |
| `bufferResponse` | Buffers (`true`) or streams (`false`) the response. | `true` |

**Policy defaults**

| Setting | Default | Defined in |
| :--- | :--- | :--- |
| Default model | `computervision` | `Priority-with-retry.xml` |
| Endpoint calls per request priority | `2` | Fragment `priorityCfg` |
| Requeue allowed | `true` | Fragment `priorityCfg` |

</details>

## Related Files

- [v3.1 release notes](3.1-notes.md)
- [Current open issues](open_issues.txt)
- [APIM policy documentation](../readme.md)
