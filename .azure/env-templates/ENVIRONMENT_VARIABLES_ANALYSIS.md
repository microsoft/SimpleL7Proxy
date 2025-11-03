# Environment Variables Cross-Check Analysis

Based on the BackendHostConfigurationExtensions.cs file, here are ALL the environment variables that SimpleL7Proxy supports:

## Complete Environment Variables List from C# Source

### Connection & Network Settings
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| DnsRefreshTimeout | 240000 | int | DNS refresh timeout in milliseconds |
| KeepAliveInitialDelaySecs | 60 | int | Initial delay before first keep-alive probe |
| KeepAlivePingIntervalSecs | 60 | int | Interval between keep-alive probes |
| KeepAliveIdleTimeoutSecs | 1200 | int | Idle timeout for keep-alive (20 minutes) |
| EnableMultipleHttp2Connections | false | bool | Enable multiple HTTP/2 connections |
| MultiConnLifetimeSecs | 3600 | int | Connection lifetime (1 hour) |
| MultiConnIdleTimeoutSecs | 300 | int | Connection idle timeout (5 minutes) |
| MultiConnMaxConns | 4000 | int | Maximum connections per server |
| IgnoreSSLCert | false | bool | Ignore SSL certificate validation |
| Port | 80 | int | Server port |

### Backend Host Configuration
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| Host1, Host2, Host3... | - | string | Backend host URLs (dynamic enumeration) |
| Probe_path1, Probe_path2... | - | string | Health probe paths for each host |
| IP1, IP2, IP3... | - | string | IP addresses for host file mapping |
| APPENDHOSTSFILE / AppendHostsFile | false | bool | Append to /etc/hosts file |
| LoadBalanceMode | "latency" | string | Load balancing mode: "latency", "roundrobin", "random" |

### Core Proxy Settings
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| Workers | 10 | int | Number of worker threads |
| MaxQueueLength | 10 | int | Maximum queue length |
| Timeout | 1200000 | int | Request timeout (20 minutes) |
| DefaultPriority | 2 | int | Default priority level |
| PollInterval | 15000 | int | Polling interval in milliseconds |
| PollTimeout | 3000 | int | Poll timeout in milliseconds |

### Priority & Queue Management
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| PriorityKeyHeader | "S7PPriorityKey" | string | Header for priority key |
| PriorityKeys | "12345,234" | string | Comma-separated priority keys |
| PriorityValues | "1,3" | string | Comma-separated priority values |
| PriorityWorkers | "2:1,3:1" | string | Priority to worker allocation mapping |

### Authentication & Authorization
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| UseOAuth | false | bool | Enable OAuth authentication |
| UseOAuthGov | false | bool | Use government OAuth endpoints |
| OAuthAudience | "" | string | OAuth audience |
| ValidateAuthAppID | false | bool | Validate authentication app ID |
| ValidateAuthAppIDHeader | "X-MS-CLIENT-PRINCIPAL-ID" | string | Header for auth app ID |
| ValidateAuthAppIDUrl | "file:auth.json" | string | URL for auth validation config |
| ValidateAuthAppFieldName | "authAppID" | string | Field name for auth app ID |

### User Management
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| UserIDFieldName | "userId" | string | Field name for user ID (migrated from LookupHeaderName) |
| LookupHeaderName | "userId" | string | Legacy field name for user ID |
| UniqueUserHeaders | "X-UserID" | string | Headers that uniquely identify users |
| UserProfileHeader | "X-UserProfile" | string | Header for user profile |
| UserPriorityThreshold | 0.1 | float | User priority threshold |
| UseProfiles | false | bool | Enable user profiles |
| UserConfigUrl | "file:config.json" | string | User configuration URL |
| SuspendedUserConfigUrl | "file:config.json" | string | Suspended user configuration URL |

### Async Operations
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| AsyncModeEnabled | false | bool | Enable async mode |
| AsyncClientRequestHeader | "AsyncMode" | string | Header for async client requests |
| AsyncClientConfigFieldName | "async-config" | string | Field name for async config |
| AsyncTimeout | 1800000 | int | Async timeout (30 minutes) |
| AsyncTriggerTimeout | 10000 | int | Async trigger timeout |
| AsyncBlobStorageAccountUri | "https://example.blob.core.windows.net/" | string | Blob storage account URI |
| AsyncBlobStorageConnectionString | "" | string | Blob storage connection string |
| AsyncBlobStorageUseMI | false | bool | Use managed identity for blob storage |
| AsyncSBConnectionString | "example-sb-connection-string" | string | Service Bus connection string |
| AsyncSBNamespace | "" | string | Service Bus namespace |
| AsyncSBQueue | "requeststatus" | string | Service Bus queue name |
| AsyncSBUseMI | false | bool | Use managed identity for Service Bus |

### Storage & Database
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| StorageDbEnabled | false | bool | Enable storage database |
| StorageDbContainerName | "Requests" | string | Storage container name |

### Circuit Breaker
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| CBErrorThreshold | 50 | int | Circuit breaker error threshold |
| CBTimeslice | 60 | int | Circuit breaker time slice |
| SuccessRate | 80 | int | Required success rate |

### Logging & Monitoring
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| LogConsole | true | bool | Enable console logging |
| LogConsoleEvent | false | bool | Enable console event logging |
| LogAllRequestHeaders | false | bool | Log all request headers |
| LogAllRequestHeadersExcept | "Authorization" | string | Headers to exclude from request logging |
| LogAllResponseHeaders | false | bool | Log all response headers |
| LogAllResponseHeadersExcept | "Api-Key" | string | Headers to exclude from response logging |
| LogHeaders | "" | string | Specific headers to log |
| LogPoller | true | bool | Enable poller logging |
| LogProbes | true | bool | Enable probe logging |

### Header Management
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| RequiredHeaders | "" | string | Required headers for requests |
| DisallowedHeaders | "" | string | Headers not allowed in requests |
| StripHeaders | "" | string | Headers to strip from requests |
| ValidateHeaders | "" | string | Headers to validate (key:value pairs) |
| TimeoutHeader | "S7PTimeout" | string | Header for timeout value |
| TTLHeader | "S7PTTL" | string | Header for TTL value |
| DependancyHeaders | "Backend-Host, Host-URL, Status, Duration, Error, Message, Request-Date, backendLog" | string | Dependency headers to add |

### Response Handling
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| AcceptableStatusCodes | "200,202,401,403,404,408,410,412,417,400" | int[] | HTTP status codes considered acceptable |
| DefaultTTLSecs | 300 | int | Default TTL in seconds |

### Container App Specific
| Variable | Default | Type | Description |
|----------|---------|------|-------------|
| CONTAINER_APP_REPLICA_NAME | "01" | string | Container app replica name |
| CONTAINER_APP_NAME | "ContainerAppName" | string | Container app name |
| CONTAINER_APP_REVISION | "revisionID" | string | Container app revision |
| TERMINATION_GRACE_PERIOD_SECONDS | 30 | int | Termination grace period |
| Hostname | {replica_id} | string | Hostname (defaults to replica ID) |
| RequestIDPrefix | "S7P" | string | Prefix for request IDs |

## Issues Found in Current Environment Templates

Several environment variables in our templates are NOT recognized by the C# code:
- `BACKEND_HOST_URLS` (should be `Host1`, `Host2`, etc.)
- `DEFAULT_REQUEST_TIMEOUT` (should be `Timeout`)
- `QUEUE_PRIORITY_LEVELS` (not a direct environment variable)
- `HTTP_CONCURRENT_REQUESTS_THRESHOLD` (not a proxy setting)
- Many Azure-specific variables that are for deployment, not runtime

## Recommendations

1. Update all environment templates to use the correct variable names
2. Remove variables that are not used by the proxy runtime
3. Add missing important variables like circuit breaker settings
4. Ensure defaults match the C# source code exactly