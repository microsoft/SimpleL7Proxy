# SimpleL7Proxy Environment Variable Templates

This directory contains predefined environment variable templates that can be used when deploying SimpleL7Proxy to Azure Container Apps. Each template is optimized for specific scenarios to help you quickly configure the proxy for your needs.

**Important:** These templates use the authoritative environment variable names from the SimpleL7Proxy C# source code. All variable names have been verified against `BackendHostConfigurationExtensions.cs`.

## Available Templates

### Standard Production (`standard-production.env`)
A balanced configuration for production workloads with good performance and reasonable resource usage. This is suitable for most production deployments.

**Key characteristics:**
- 15 workers with balanced priority allocation
- 20-minute timeout for production requests
- Async operations enabled for scalability
- Moderate logging for monitoring
- Circuit breaker with balanced thresholds
- Multiple HTTP/2 connections enabled

### High Performance (`high-performance.env`)
Optimized for maximum throughput and low latency. Use this configuration when handling high volumes of traffic where performance is critical.

**Key characteristics:**
- 25 workers with optimized allocation
- Large queue (50) for burst handling
- Multiple priority levels (0-3) for fine control
- Aggressive connection pooling (10,000 max connections)
- Fast circuit breaking (30% threshold)
- Minimal logging to reduce I/O overhead

### Cost Optimized (`cost-optimized.env`)
Designed to minimize resource usage and cost. Suitable for deployments with lower traffic volumes or where budget constraints are important.

**Key characteristics:**
- 5 workers to minimize resource usage
- Small queue (5) to reduce memory
- Simplified priority system (2 levels)
- Conservative circuit breaker (70% threshold)
- Async operations disabled
- Minimal logging and monitoring

### High Availability (`high-availability.env`)
Maximized for resilience and uptime. Use this configuration when the proxy must maintain availability even during backend failures.

**Key characteristics:**
- 20 workers with redundancy focus
- Large queue (30) for failover scenarios
- Enhanced monitoring and logging
- Conservative circuit breaker (30% threshold)
- Authentication and validation enabled
- Extended timeouts for retry scenarios

### Local Development (`local-development.env`)
Configured for running SimpleL7Proxy locally with `dotnet run`. Optimized for local development workflows.

**Key characteristics:**
- 5 workers optimized for local machine
- Large queue (40,000) for local testing
- Short timeout (100 seconds) for quick feedback
- Localhost backends (port 3000)
- Real Azure resource connections (Service Bus, Blob Storage)
- File logging enabled
- Specific test headers and validation rules

### Container Development (`container-development.env`)
Configured for containerized development and testing scenarios. Use when running in Docker containers or Azure Container Apps.

**Key characteristics:**
- 8 workers for container workloads
- Comprehensive logging enabled
- Extended timeouts (30 minutes) for debugging
- Round-robin load balancing for testing
- Lenient circuit breaker (80% threshold)
- All error status codes accepted for testing

## Environment Variable Categories

### Core Proxy Settings
- `Workers`: Number of worker threads
- `MaxQueueLength`: Maximum queue size
- `Timeout`: Request timeout in milliseconds
- `PollInterval`: Health check polling interval
- `LoadBalanceMode`: Load balancing strategy (latency, roundrobin, random)

### Backend Configuration
- `Host1`, `Host2`, etc.: Backend host URLs
- `Probe_path1`, `Probe_path2`, etc.: Health check endpoints
- `IP1`, `IP2`, etc.: IP addresses for host file mapping

### Priority Management
- `DefaultPriority`: Default priority level
- `PriorityKeys`: Comma-separated priority keys
- `PriorityValues`: Corresponding priority values
- `PriorityWorkers`: Worker allocation per priority (format: "priority:count,...")

### Circuit Breaker
- `CBErrorThreshold`: Error percentage threshold
- `CBTimeslice`: Time window for error calculation
- `SuccessRate`: Required success rate

### Logging
- `LogConsole`: Enable console logging
- `LogAllRequestHeaders`: Log all request headers
- `LogAllResponseHeaders`: Log all response headers
- `LogHeaders`: Specific headers to log

## Using the Templates

### Option 1: Apply during deployment

Use these templates during the Azure Container App deployment by specifying the template file:

```bash
# Deploy using standard production settings
./deploy.sh --env-template standard-production.env
```

### Option 2: Apply to an existing Container App

Apply these environment variables to an existing Container App:

```bash
# Using Azure CLI with JSON format
az containerapp update \
  --name your-container-app \
  --resource-group your-resource-group \
  --set-env-vars @.azure/env-templates/high-performance.env
```

### Option 3: Use as a reference

You can also use these templates as a reference when manually configuring your container app environment variables through the Azure Portal.

## Customizing Templates

To customize a template:

1. Create a copy of the template file
2. Modify the values to suit your needs
3. **Important**: Set your actual backend hosts by uncommenting and updating:
   ```
   Host1=https://your-actual-backend.example.com
   Host2=https://your-backup-backend.example.com
   Probe_path1=/health
   Probe_path2=/health
   ```
4. Save with a descriptive name
5. Use the custom template during deployment

## Environment Variable Reference

For detailed information about each environment variable and its impact, refer to:
- [Environment Variables Documentation](../../docs/ENVIRONMENT_VARIABLES.md)
- [Environment Variables Analysis](./ENVIRONMENT_VARIABLES_ANALYSIS.md) - Complete list from C# source code

## Important Notes

- All environment variable names in these templates match exactly with the SimpleL7Proxy C# source code
- You **must** configure your actual backend hosts (`Host1`, `Host2`, etc.) before deployment
- The proxy requires at least one backend host to function properly
- Some variables have interdependencies (e.g., `PriorityKeys` and `PriorityValues` must have matching counts)