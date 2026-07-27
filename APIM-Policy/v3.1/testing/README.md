# v3.1 APIM Policy Integration Tests

This test surface runs the current v3.1 retry policy in a dedicated APIM API while SimpleL7Proxy records every attempt to local NDJSON.

## Test Boundary

```text
PolicyScenarioIntegrationTests
  -> local SimpleL7Proxy
  -> dedicated APIM API
  -> v3.1 Priority-with-retry policy
  -> deployed LLM Simulator slot A/B
```

The LLM Simulator's `validate.sh` is a simulator smoke test. It does not validate APIM policy behavior.

## Create the APIM Test API

1. Deploy the current `test/LLMSimulator` Function App.
2. In APIM, create a named value **before creating the policy fragment**. Use these exact fields:

  | Field | Value |
  | :--- | :--- |
  | Display name | `policy-test-simulator-base-url` |
  | Name | `policy-test-simulator-base-url` |
  | Type | `Plain` |
  | Value | Simulator origin without `/api` or a trailing slash |

  The fragment reference `{{policy-test-simulator-base-url}}` resolves the **Display name**. APIM rejects the fragment with `Cannot find a property 'policy-test-simulator-base-url'` until that named value exists.

3. Set the named value to the simulator origin, for example:

   ```text
   https://example-simulator.azurewebsites.net
   ```

4. Create the policy fragment `endpoint_selection_frag_policy_test` from [endpoint_selection_frag_policy_test.xml](endpoint_selection_frag_policy_test.xml).
5. Import [policy-test-api.openapi.yaml](policy-test-api.openapi.yaml) as a new API with suffix `policytest`.

The imported API has exactly two operations:

```text
POST    /{caseId}/{specA}/{specB}/openai/v1/chat/completions
OPTIONS /reset
```

## Install Operation Policies

### POST runPolicyScenario

Copy [../Priority-with-retry.xml](../Priority-with-retry.xml) into the POST operation policy editor.

Change exactly one value:

```xml
<include-fragment fragment-id="endpoint_selection_frag_30" />
```

to:

```xml
<include-fragment fragment-id="endpoint_selection_frag_policy_test" />
```

Do not change retry logic, cache keys, expressions, headers, or `<base />`. This keeps the policy under test identical to the current v3.1 policy except for its backend catalog.

### OPTIONS resetPolicyState

Install [reset-policy.xml](reset-policy.xml) on the OPTIONS operation. This operation intentionally has no `<base />`. It removes `throttle-{context.Api.Id}` and treats an absent entry as already reset, then returns:

```text
HTTP 204
X-Policy-Test-Reset: true
```

The OPTIONS and POST operations share the same API ID, so reset removes the exact cache entry used by the POST policy.

## Run Through SimpleL7Proxy

Edit the gitignored local configuration file:

```text
test/RegressionTests/configs/policy-test.local.json
```

Set the APIM API suffix as a proxy-relative route in `testEnvironment`:

```json
"POLICY_TEST_APIM_URL": "policytest"
```

Configure APIM connectivity and authentication in `proxyEnvironment` using the normal proxy host descriptor:

```json
"Host_apim": "host=https://<apim-name>.azure-api.net; path=/; processor=MultiLineAllUsage; api-key-header=<header>; api-key=<key>; mode=apim; probe=/<probe>; enabled=true"
```

The harness starts the proxy on a random localhost port and sends requests to:

```text
http://127.0.0.1:<random-port>/<POLICY_TEST_APIM_URL>/...
```

The proxy then selects `Host_apim` and forwards the request to APIM. The harness does not derive or override an APIM host.

The local file is excluded by `.gitignore` because `Host_apim` can contain credentials. Shell environment values override matching JSON values. Set `POLICY_TEST_CONFIG_PATH` to load a different file.

Keep `"POLICY_TEST_SCENARIO": "healthy-200"` while checking connectivity. Set it to an empty string to run the complete catalog.

Run only the policy suite:

```bash
bash test/RegressionTests/run-policy-scenarios.sh
```

The `proxyEnvironment` object is copied into the child proxy before startup. The harness owns only the random `Port`, per-scenario `LOGFILE_NAME`, and request `Timeout`.

When the local config or `POLICY_TEST_APIM_URL` is absent, MSTest reports the suite as inconclusive. It never substitutes a direct simulator call.

## Evidence

The runner starts a clean proxy for each scenario with:

```text
EVENT_LOGGERS=file
LogToEvents=backend,proxy
LOGFILE_NAME=<scenario-artifacts>/events.ndjson
```

The proxy emits newline-delimited JSON. The runner correlates records using response `S7P-ID`, response `x-MID`, event identifiers, and the unique scenario path:

- `S7P-BackendRequest` records preserve failed and requeued APIM attempts.
- `S7P-ProxyRequest` preserves the final APIM response.
- Failed attempts are also flattened as `Attempt-N-*` fields.
- `N/A` telemetry values are ignored when a failed-attempt field contains the actual value.
- Requeue can clear `incompleteRequests`, so the original `S7P-BackendRequest` records remain authoritative.

On failure, the test retains:

```text
events.ndjson
proxy.stdout.log
proxy.stderr.log
response.json
```

## Policy Configurations

The test fragment provides these `x-LLMModel` configurations:

The runner puts the model in the JSON request body. SimpleL7Proxy detects it and adds one `x-LLMModel` header to the APIM request.

| Model | Purpose |
| :--- | :--- |
| `policy-default` | Buffered SIM-A to SIM-B selection and failover |
| `policy-context` | PTU/PAYGO labels for context-window failover |
| `policy-timeout` | SIM-A timeout 1 second, SIM-B timeout 5 seconds |
| `policy-stream` | Unbuffered streaming responses |
| `policy-limit` | SIM-A `limitConcurrency=low` |
| `policy-priority` | SIM-A accepts priority 1; SIM-B accepts priority 2 |
| `policy-auth` | API key on SIM-A, no auth on SIM-B |
| `policy-mi` | Managed Identity on SIM-A, no auth on SIM-B |

Priority behavior is fixed for deterministic tests:

| Proxy priority key | APIM priority | Backend-call budget | Requeue |
| :--- | :--- | :--- | :--- |
| `high` | 1 | 2 | false |
| `medium` | 2 | 2 | true |
| `low` | 3 | 1 | false |

## Scenario Catalog

The data-driven cases are in [../../../test/RegressionTests/configs/policy-scenarios.json](../../../test/RegressionTests/configs/policy-scenarios.json). Each case defines slot A and B responses plus expected HTTP status, backend-call count, required `backendLog` entries, and forbidden entries.

The suite currently covers normal 200, 201, 204, empty-200 behavior, permanent 400/401, context-window failover, 429 header variants, temporary 500, exhaustion, API-key clearing, and streaming.
