# Scenario 1: Local Development Environment

## Overview

This deployment scenario creates a complete local testing environment for SimpleL7Proxy. It demonstrates the core proxy functionality by setting up multiple backend servers and routing requests between them. This is the quickest way to see SimpleL7Proxy in action and understand its basic load balancing and failover capabilities.

## Why This Setup is Necessary

### Learning and Development
- **Hands-on Experience**: Provides immediate feedback on how the proxy routes requests
- **Configuration Testing**: Safe environment to experiment with different proxy settings
- **Feature Validation**: Test priority queuing, load balancing algorithms, and failover behavior
- **Performance Analysis**: Observe request routing decisions and response times

### Architecture Validation
- **Multi-Backend Simulation**: Mimics real-world scenarios with multiple backend services
- **Load Balancing Verification**: See how the proxy selects the fastest backend
- **Failover Testing**: Stop/start backends to test automatic failover capabilities
- **Request Flow Understanding**: Trace how requests move through the proxy to backends

## Setup Requirements

* Dotnet 9
* Python ( Optional )
This setup requires **four terminal windows** running simultaneously:

### Window 1: Backend Server (Port 3000)
**Purpose**: First backend server that will respond to proxied requests
- Simulates a backend service (e.g., web API, microservice)
- Provides baseline response times for comparison

<details>
<summary><strong>🟢 .NET Version (Recommended)</strong></summary>

```bash
cd <ROOT>/test/nullserver/nullserver
dotnet run --urls="http://localhost:3000"
```
</details>

<details>
<summary><strong>🐍 Python Version (Alternative)</strong></summary>

```bash
cd <ROOT>/test/nullserver/Python
PORT=3000 python3 server.py
```
</details>

### Window 2: Backend Server (Port 3001)  
**Purpose**: Second backend server for load balancing demonstration
- Creates a multi-backend scenario
- Allows testing of intelligent routing between different response times
- Enables failover testing when one backend is stopped

<details>
<summary><strong>🟢 .NET Version (Recommended)</strong></summary>

```bash
cd <ROOT>/test/nullserver/nullserver
dotnet run --urls="http://localhost:3001"
```
</details>

<details>
<summary><strong>🐍 Python Version (Alternative)</strong></summary>

```bash
cd <ROOT>/test/nullserver/Python
PORT=3001 python3 server.py
```
</details>

### Window 3: SimpleL7Proxy
**Purpose**: The main proxy service that routes requests
- Monitors both backend servers for availability and response times
- Makes intelligent routing decisions based on latency
- Provides detailed logging and telemetry

```bash
cd <ROOT>/src
# Set environment variables for local testing
export BACKEND_HOSTS="http://localhost:3000,http://localhost:3001"
export PROXY_PORT=8080
export LOG_LEVEL=Information

dotnet run
```

### Window 4: Client Requests
**Purpose**: Simulates client applications sending requests
- Uses curl commands to send various types of requests
- Demonstrates different routing scenarios
- Tests priority handling and load distribution

<details>
<summary><strong>📋 Basic Testing Commands</strong></summary>

```bash
# Basic request to see load balancing
curl http://localhost:8080/api/test

# Multiple requests to observe routing patterns
for i in {1..10}; do curl http://localhost:8080/api/test; echo; done

# Test with different paths
curl http://localhost:8080/health
curl http://localhost:8080/api/data
```
</details>

<details>
<summary><strong>🔥 Priority Testing Commands</strong></summary>

```bash
# High priority request
curl -H "X-Priority: High" http://localhost:8080/api/test

# Low priority request
curl -H "X-Priority: Low" http://localhost:8080/api/test

# Mixed priority load test
for i in {1..5}; do 
  curl -H "X-Priority: High" http://localhost:8080/api/test &
  curl -H "X-Priority: Low" http://localhost:8080/api/test &
done
wait
```
</details>

<details>
<summary><strong>⚡ Failover Testing Commands</strong></summary>

```bash
# 1. Start sending continuous requests
while true; do curl http://localhost:8080/api/test; sleep 1; done

# 2. In another terminal, stop one backend server (Ctrl+C in Window 1 or 2)
# 3. Observe requests continue to work (failover)
# 4. Restart the stopped backend
# 5. Watch it rejoin the load balancing pool
```
</details>

## What You'll Observe

### Intelligent Load Balancing
- Watch as the proxy automatically selects the fastest backend
- See how it adapts when backend performance changes
- Observe round-robin vs. latency-based routing differences

### Automatic Failover
- Stop one backend server and see requests automatically route to the healthy one
- Restart the backend and watch it rejoin the load balancing pool
- Zero downtime during backend failures

### Request Prioritization
- Send requests with different priority levels
- Observe how high-priority requests preempt lower-priority ones
- Test anti-starvation protection for low-priority requests

### Detailed Telemetry
- Monitor request processing times, queue duration, and worker assignments
- Track backend health and response times
- View comprehensive logging for debugging and optimization

## Getting Started

1. **Open Four Terminals**: Position them so you can see all simultaneously
2. **Start Backend Servers**: Launch both null servers on ports 3000 and 3001
3. **Configure Proxy**: Set environment variables for local development
4. **Start Proxy**: Launch SimpleL7Proxy with your configuration
5. **Send Requests**: Use curl commands to test various scenarios

This setup provides the foundation for understanding SimpleL7Proxy's capabilities before deploying to production environments.

## Next Steps

After mastering this basic scenario:
- Try different proxy configurations through environment variables
- Test with containerized deployments
- Explore advanced features like user profiles and async processing
- Move to cloud deployment scenarios for production testing
