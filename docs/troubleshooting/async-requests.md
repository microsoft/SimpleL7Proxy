# Async Requests Not Completing

> **TL;DR**
> 1. Check that `AsyncModeEnabled=true` and the request sends the `S7PAsyncMode` header.
> 2. Verify the user profile has async configuration with a valid blob container and Service Bus topic.
> 3. If blobs exist but are empty, the critical cause is `OutputStream` not being cleared — see below.

---

## How async mode works

```
Client sends request with S7PAsyncMode header
    │
    ▼
Proxy processes normally → if processing exceeds AsyncTriggerTimeout:
    │
    ▼
Proxy returns 202 immediately
    │ (blob URI + SB topic in response body)
    ▼
AsyncWorker writes response → Blob Storage
    │
    ▼
Status update written → Service Bus topic
```

---

## Step 1 — Verify async is enabled

All three must be true for async to activate:

| Condition | Setting | Env Var |
|-----------|---------|---------|
| System switch on | `AsyncModeEnabled=true` | `AsyncModeEnabled` |
| Request header present | Header name = `AsyncClientRequestHeader` (default `S7PAsyncMode`) | set on request |
| User profile has async config | Profile field `async-config` contains blob container + SB topic | — |

---

## Step 2 — Check the trigger timeout

`AsyncTriggerTimeout` (default 10 s) is the time a request must be in flight before async kicks in. If the backend responds in under 10 s, the request is returned **synchronously** — this is normal.

To force a request to go async sooner, reduce `AsyncTriggerTimeout`:

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Trigger timeout (ms) | `AsyncTriggerTimeout=<ms>` | `Warm:Async:TriggerTimeout` |

---

## Step 3 — Check blob storage configuration

`AsyncBlobStorageConfig` is a composite string:

```bash
AsyncBlobStorageConfig=uri=https://mystorageaccount.blob.core.windows.net,mi=true
```

For managed identity (`mi=true`), the proxy identity needs **`Storage Blob Data Contributor`** on the storage account.

```bash
az role assignment create \
  --assignee <principal-id> \
  --role "Storage Blob Data Contributor" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Storage/storageAccounts/<account>"
```

---

## Step 4 — Check Service Bus configuration

`AsyncSBConfig` is a composite string:

```bash
AsyncSBConfig=cs=<connection-string>,ns=<namespace>,q=requeststatus,mi=true
```

For managed identity, the proxy identity needs **`Azure Service Bus Data Sender`** on the namespace or topic.

---

## Symptom: blobs exist but are empty

> [!WARNING]
> **Critical bug (fixed Feb 2026):** After the proxy sends the 202 response and closes the client connection, `OutputStream` must be explicitly set to `null`. If it is not, `GetOrCreateDataStreamAsync()` returns the already-closed client stream instead of opening a blob stream, and the backend response is written to nothing.

If you are running a custom build or patched version, verify [AsyncWorker.cs](../../src/SimpleL7Proxy/Proxy/AsyncWorker.cs) contains:

```csharp
_requestData.Context.Response.Close();
_requestData.OutputStream = null;  // ← CRITICAL — must follow Close()
```

This line must appear **after** `Response.Close()`. Without it, the blob will be created but will remain empty.

---

## Symptom: 202 never arrives — request returns synchronously

- `AsyncTriggerTimeout` may be larger than the backend response time. The backend responded before async was triggered.
- Verify the client is sending the `S7PAsyncMode` header.

## Symptom: 202 arrives but blob URI is never populated

- Check Service Bus connectivity — the status message (with the blob URI) may not be reaching the client.
- Check blob container name in the user profile `async-config` field.
- Check `AsyncBlobWorkerCount` — if set to 0 or the workers are all busy, blobs queue up.

## Symptom: async request times out with no blob

`AsyncTimeout` (default 30 min) is the maximum async request lifetime. After this, the request is abandoned. If the backend takes longer than 30 min, increase `AsyncTimeout`.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Max async lifetime (ms) | `AsyncTimeout=<ms>` | `Warm:Async:Timeout` |
| Async request TTL after upgrade | `AsyncTTLSecs=<s>` | `Warm:Async:TTLSecs` |

---

## Related

- [AsyncOperation.md](../AsyncOperation.md) — full async configuration reference
- [USER_PROFILES.md](../USER_PROFILES.md) — how to configure async per user profile
- [TIMEOUTS.md](../TIMEOUTS.md) — TTL and timeout interactions
