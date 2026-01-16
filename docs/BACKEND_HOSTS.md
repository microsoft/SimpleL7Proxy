# Backend Host Configuration

SimpleL7Proxy supports defining backend hosts using two methods: a strictly environment-variable-based legacy method and a more flexible connection-string-based method. You can configure up to 9 backend hosts using variables `Host1` through `Host9`.

## Configuration Methods

### 1. Advanced Connection String (Recommended)

This method allows you to define all properties for a single host within the `HostN` environment variable itself. It uses a semicolon-separated key=value format.

**Format:**
`Host1="host=https://api.backend.com;probe=/health;mode=apim"`

**Supported Keys:**

| Key | Description | Default |
|-----|-------------|---------|
| **host** | The base URL of the backend service. | (Required) |
| **probe** | The path to the health probe endpoint (e.g., `/health`, `/status`). | `echo/resource?param1=sample` |
| **ipaddress** | Overrides the DNS resolution by forcing a specific IP address for requests. | (Empty) |
| **mode** | Connectivity mode. Use `direct` to skip APIM-style handling and probes, or omit for standard proxying. | Standard/APIM |
| **path** | A partial path to append to requests or match against (depending on internal routing logic). | `/` |
| **processor** | Specifies a custom request processor if available. | (Empty) |
| **useoauth** / **usemi** | If `true`, enables Managed Identity/OAuth authentication for this host. | `false` |
| **audience** | The expected audience claim for OAuth tokens. | (Empty) |
| **retryafter** | If `true`, respects the `Retry-After` header from the backend. | `true` |

**Examples:**

*   **Standard Service with Health Check:**
    ```bash
    Host1="host=https://my-api.internal;probe=/api/health"
    ```

*   **Service with IP Override (No DNS):**
    ```bash
    Host2="host=https://my-api.internal;ipaddress=10.0.1.5;probe=/health"
    ```

*   **Direct Mode (No Health Probes):**
    Useful for simple forwarding or testing where active health checking is not required.
    ```bash
    Host3="host=https://simple-service.internal;mode=direct"
    ```

*   **Authenticated Service:**
    ```bash
    Host4="host=https://secure-api.internal;usemi=true;audience=api://my-app-id"
    ```

### 3. Direct Mode (Serverless/On-Demand)

Direct Mode is designed for backends where active health probing is undesirable (e.g., Azure Container Apps, Azure Functions, or other serverless endpoints that scale to zero). By disabling probes, you avoid waking up the service unnecessarily.

**Mechanism:**
*   **No Active Probing**: The proxy will **never** send health probe requests to the backend.
*   **Always Healthy**: The host is assumed to be 100% available (`SuccessRate = 1.0`).
*   **Latency Load Balancing**: Since no probe latency is recorded, the "Average Latency" for these hosts defaults to 0. This means they will effectively be treated equally (Round Robin) amongst themselves and prioritized over high-latency probed hosts.

**Required Keys:**
To enable this mode, your connection string **must** include:
*   `mode=direct`
*   `host`
*   `path` (The base path to route to)
*   `processor` (If a specific request processor logic is required, otherwise standard)

**Example:**
```bash
Host5="host=https://my-func.azurewebsites.net;mode=direct;path=/api/v1;processor=OpenAI"
```

### 4. Legacy Simple Configuration

This method splits configuration across multiple environment variables (`HostN`, `Probe_pathN`, `IPN`). It is simpler but less flexible than the connection string format.

| Variable | Description |
|----------|-------------|
| **HostN** | The base URL. Example: `https://api.backend.com` |
| **Probe_pathN** | The health probe path. Example: `/health` |
| **IPN** | (Optional) The IP address to use instead of DNS resolution. |

**Example:**

```bash
Host1="https://api.backend.com"
Probe_path1="/health"
IP1="10.0.1.5"
```

## Mixing Methods

You can mix methods across different hosts (e.g., `Host1` uses a connection string, `Host2` uses the legacy format), but you should not mix definitions for the *same* host number. If `Host1` is a connection string, `Probe_path1` and `IP1` will be ignored.
