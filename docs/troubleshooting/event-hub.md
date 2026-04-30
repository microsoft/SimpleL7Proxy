# Event Hub — Messages Not Appearing

> **TL;DR**
> 1. Set `EVENT_LOGGERS` to include `eventhub`.
> 2. Provide either a connection string **or** a namespace (managed identity) — not both.
> 3. For managed identity, assign the **`Azure Event Hubs Data Sender`** role.
> 4. All Event Hub settings are **Cold** — a restart is required after any change.

---

## Step 1 — Enable the Event Hub backend

Set each value using **either** an environment variable **or** an Azure App Configuration key — not both.
Use environment variables for simple deployments; use App Configuration when managing settings centrally across multiple instances.

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Enable Event Hub logging | `EVENT_LOGGERS=eventhub` | `Cold:Logging:EventLoggers` |

To enable both file and Event Hub logging simultaneously, set the value to `file,eventhub`.

---

## Step 2 — Provide connection details (choose one)

### Option A — Connection string *(simpler, no managed identity required)*

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Connection string | `EVENTHUB_CONNECTIONSTRING=<value>` | `Cold:Logging:EventHub:ConnectionString` |
| Hub name | `EVENTHUB_NAME=<value>` | `Cold:Logging:EventHub:Name` |

### Option B — Managed identity / RBAC *(recommended for production)*

| Setting | Env Var | App Config key |
|---------|---------|----------------|
| Namespace | `EVENTHUB_NAMESPACE=<value>` | `Cold:Logging:EventHub:Namespace` |
| Hub name | `EVENTHUB_NAME=<value>` | `Cold:Logging:EventHub:Name` |

`EVENTHUB_NAMESPACE` accepts either the short name (e.g. `myns`) or the full hostname (e.g. `myns.servicebus.windows.net`).

The identity running the proxy (managed identity, workload identity, or service principal) must have the **`Azure Event Hubs Data Sender`** role on the Event Hub or its parent namespace.

```bash
az role assignment create \
  --assignee <principal-id> \
  --role "Azure Event Hubs Data Sender" \
  --scope "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.EventHub/namespaces/<namespace>/eventhubs/<hub-name>"
```

---

## Verifying the connection

Check logs at startup for `[EVENT HUB]` entries:

```
[EVENT HUB] connecting via connection string, eventhubname: <name>
```

or, for managed identity:

```
[EVENT HUB] connecting via managed identity, namespace: <ns>
```

If neither appears, `EVENT_LOGGERS` may not include `eventhub`, or the setting change has not taken effect yet (restart required).

> [!NOTE]
> If the Event Hub connection fails at startup, the backend is **silently disabled** and other configured backends (e.g., `file`) continue unaffected. Verify the connection string or role assignment and restart.

> [!TIP]
> **Sovereign cloud:** if your namespace ends in `.servicebus.usgovcloudapi.net`, set `EVENTHUB_NAMESPACE` to the full hostname — the proxy uses it as-is and will not append `.servicebus.windows.net`.

---

## Related

- [OBSERVABILITY.md](../OBSERVABILITY.md) — full Event Hub architecture and custom loggers
- [ENVIRONMENT_VARIABLES.md](../ENVIRONMENT_VARIABLES.md) — all `EVENTHUB_*` variables
- [AZURE_APP_CONFIGURATION.md](../AZURE_APP_CONFIGURATION.md) — how to set Cold settings in App Config
