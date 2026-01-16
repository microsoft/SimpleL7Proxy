# Development and Testing

This document provides guidance for setting up SimpleL7Proxy for development and testing purposes.

## Local Development Setup

### Prerequisites
- .NET SDK 9.0 or later
- Git (for cloning the repository)
- Optional: Docker (for containerized testing)

### Quick Start for Development

1. **Clone the repository:**
```bash
git clone https://github.com/your-org/SimpleL7Proxy.git
cd SimpleL7Proxy
```

2. **Set up environment variables:**
```bash
# Essential development configuration
export Port=8080
export Host1=http://localhost:3000
export Host2=http://localhost:5000
export Timeout=5000
export LogAllRequestHeaders=true
export LogAllResponseHeaders=true
export LOGFILE=dev-events.log
export Workers=5
export IgnoreSSLCert=true
```

3. **Run the proxy:**
```bash
dotnet run
```

### Testing with Mock Backends

For testing without real backend services, you can use simple HTTP servers:

#### Using Node.js (if available)
```bash
# Terminal 1 - Mock backend on port 3000
npx http-server -p 3000 -c-1

# Terminal 2 - Mock backend on port 5000  
npx http-server -p 5000 -c-1

# Terminal 3 - Run the proxy
dotnet run
```

#### Using Python (if available)
```bash
# Terminal 1 - Mock backend on port 3000
python -m http.server 3000

# Terminal 2 - Mock backend on port 5000
python -m http.server 5000

# Terminal 3 - Run the proxy
dotnet run
```

### Development Configuration

Create a local `appsettings.Development.json` file:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ProxyOptions": {
    "Port": 8080,
    "Host1": "http://localhost:3000",
    "Host2": "http://localhost:5000",
    "Workers": 5,
    "MaxQueueLength": 10,
    "LogAllRequestHeaders": true,
    "LogAllResponseHeaders": true,
    "LogProbes": true,
    "IgnoreSSLCert": true,
    "PollInterval": 5000,
    "Timeout": 3000
  }
}
```

## Testing Scenarios

### Load Testing

Use tools like `wrk` or `curl` to test the proxy:

```bash
# Simple load test with curl
for i in {1..100}; do
  curl -H "X-Test-Request: $i" http://localhost:8080/test &
done
wait

# Using wrk (if installed)
wrk -t4 -c100 -d30s http://localhost:8080/
```

### Priority Testing

Test priority-based routing:

```bash
# High priority request
curl -H "S7PPriorityKey: 12345" http://localhost:8080/high-priority

# Normal priority request
curl http://localhost:8080/normal-priority

# With custom TTL
curl -H "S7PTTL: 60" http://localhost:8080/with-ttl
```

### Async Mode Testing

When testing async functionality:

```bash
# Set up async environment variables
export AsyncModeEnabled=true
export AsyncBlobStorageConnectionString="your-blob-connection-string"
export AsyncSBConnectionString="your-servicebus-connection-string"

# Test async request
curl -H "AsyncMode: true" -H "X-UserID: test-user" http://localhost:8080/async-test
```

## Debugging

### Enable Debug Logging

```bash
export LogAllRequestHeaders=true
export LogAllResponseHeaders=true
export LogProbes=true
export LOGFILE=debug.log
```

### Request-Level Debugging

Add the debug header to individual requests:

```bash
curl -H "S7PDEBUG: true" http://localhost:8080/debug-request
```

### Health Check Testing

Test backend health monitoring:

```bash
# Check if proxy is detecting backend health
curl http://localhost:8080/health

# Test failover by stopping one backend
# Stop backend on port 3000, then test
curl http://localhost:8080/test-failover
```

## Container Development

### Build and Test Locally

```bash
# Build the container
docker build -t proxy-dev -f Dockerfile .

# Run with development configuration
docker run -p 8080:443 \
  -e "Host1=http://host.docker.internal:3000" \
  -e "Host2=http://host.docker.internal:5000" \
  -e "LogAllRequestHeaders=true" \
  -e "Workers=5" \
  -e "LOGFILE=/tmp/events.log" \
  proxy-dev
```

### Docker Compose for Development

Create a `docker-compose.dev.yml`:

```yaml
version: '3.8'
services:
  proxy:
    build: .
    ports:
      - "8080:443"
    environment:
      - Host1=http://backend1:3000
      - Host2=http://backend2:5000
      - LogAllRequestHeaders=true
      - Workers=5
    depends_on:
      - backend1
      - backend2
  
  backend1:
    image: nginx:alpine
    ports:
      - "3000:80"
  
  backend2:
    image: nginx:alpine
    ports:
      - "5000:80"
```

Run with: `docker-compose -f docker-compose.dev.yml up`

## Common Development Issues

### Port Conflicts
If port 8080 is in use, change the port:
```bash
export Port=8081
```

### SSL Certificate Issues
For development with self-signed certificates:
```bash
export IgnoreSSLCert=true
```

### Backend Connection Issues
Ensure backend services are running and accessible:
```bash
curl http://localhost:3000/health
curl http://localhost:5000/health
```

## IDE Configuration

### Visual Studio Code

Create `.vscode/launch.json`:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": ".NET Core Launch (web)",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/bin/Debug/net10.0/SimpleL7Proxy.dll",
      "args": [],
      "cwd": "${workspaceFolder}",
      "stopAtEntry": false,
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "Port": "8080",
        "Host1": "http://localhost:3000",
        "Host2": "http://localhost:5000",
        "LogAllRequestHeaders": "true"
      }
    }
  ]
}
```
