# Configuration Scenarios

This document provides various configuration examples for different deployment scenarios using SimpleL7Proxy.

## Basic Setup

### Minimal Configuration
The simplest setup requires just a few environment variables:

```bash
Port=8000
Host1=https://localhost:3000
Host2=http://localhost:5000
PollInterval=1500
Timeout=3000
```

This creates a listener on port 8000 and checks the health of two hosts every 1.5 seconds. Incoming requests are proxied to the server with the lowest latency. Requests timeout after 3 seconds.

## High Availability Configuration

For production environments requiring high availability, configure multiple hosts with strict health monitoring:

```bash
# Backend configuration for high availability
Host1=https://primary.example.com
Host2=https://secondary.example.com
Host3=https://tertiary.example.com
Probe_path1=/health
Probe_path2=/health
Probe_path3=/health
PollInterval=5000
CBErrorThreshold=20
SuccessRate=95
Workers=20
MaxQueueLength=50
```

## Security-Focused Configuration

For environments requiring strict security controls:

```bash
# Security-focused configuration
DisallowedHeaders=X-Forwarded-For,X-Real-IP
RequiredHeaders=Authorization,X-API-Key
ValidateAuthAppID=true
ValidateAuthAppIDUrl=file:auth.json
LogAllRequestHeaders=false
LogAllRequestHeadersExcept=Authorization,X-API-Key,Cookie
UseOAuth=true
OAuthAudience=api://your-app-id
```

## Performance-Optimized Configuration

For high-throughput scenarios:

```bash
# Performance optimization
Workers=50
MaxQueueLength=100
EnableMultipleHttp2Connections=true
MultiConnMaxConns=8000
KeepAliveIdleTimeoutSecs=1800
PollInterval=10000
Timeout=5000
```

## Async Processing Configuration

For long-running request processing:

```bash
# Service Level Configuration
AsyncModeEnabled=true
AsyncBlobStorageConnectionString=DefaultEndpointsProtocol=https;AccountName=...
AsyncSBConnectionString=Endpoint=sb://...
AsyncTimeout=1800000

# Logging for async operations
LogAllRequestHeaders=true
LogAllResponseHeaders=true
APPINSIGHTS_CONNECTIONSTRING=InstrumentationKey=...
```

## Regional Deployment Configuration

For multi-region deployments:

```bash
# Regional configuration
Host1=https://us-east.example.com
Host2=https://us-west.example.com
Host3=https://eu-west.example.com
Probe_path1=/health?region=us-east
Probe_path2=/health?region=us-west
Probe_path3=/health?region=eu-west
DnsRefreshTimeout=60000
PollInterval=8000
```

## Container Apps Configuration

When deploying to Azure Container Apps:

```bash
# Container Apps specific
CONTAINER_APP_NAME=simplel7proxy
Port=443
Workers=15
MaxQueueLength=30
APPINSIGHTS_CONNECTIONSTRING=${APPINSIGHTS_CONNECTION_STRING}
LogAllRequestHeaders=true
LogConsoleEvent=true
```

## Development Configuration

For development environments with verbose logging:

```bash
# Development configuration
Port=8080
Host1=http://localhost:3000
Host2=http://localhost:5000
LogAllRequestHeaders=true
LogAllResponseHeaders=true
LogProbes=true
LOGFILE=dev-events.log
Workers=5
MaxQueueLength=10
IgnoreSSLCert=true
```
