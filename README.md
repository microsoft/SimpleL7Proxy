# SimpleL7Proxy

Enterprise-grade HTTP/HTTPS proxy offers high availability, smart load balancing with failover, priority traffic control, advanced security, circuit breaking, async support, deep observability, and gov. cloud compliance. 

This codebase implements the following scenarios when used with APIM and OpenAI.

## Proxy Scenarios:
* 100% internal VNET traffic with Managed Identity and OAuth2 authentication.
* Prioritize urgent requests to ensure critical workloads are served first.
* Prevent resource monopolization by limiting greedy users.
* Distribute traffic across multiple regions for resilience and performance.
* Automatically retry and fail over during outages or backend errors.
* Seamlessly support both synchronous and asynchronous client patterns.
* Capture AI token metrics even if using streaming.
* Enforce strict header validation before processing requests.
* Restrict or suspend user or group access based on policy.
* Throttle or reject requests when circuit breaker thresholds are reached.

## APIM Policy Scenarios:
* Route high-priority requests to designated backend services.
* Sustain high throughput, exceeding 23M TPM.
* Control concurrency for each backend independently.
* Enable streaming with real-time token capture.
* Enforce backend timeouts to ensure responsiveness.
* Maximize PTU  usage, while choosing which priorities use PayGo.

# Arch Diagram
![Architecture Diagram](docs/arch.png)

Documentation is located in the [docs/README.md](docs/README.md) file.

## Local Development Setup

For local development and testing, SimpleL7Proxy includes a setup script that configures the proxy to run on your local machine:

```bash
# Navigate to the .azure directory and run the setup script
cd .azure
./local-setup.sh
```

The `local-setup.sh` script provides an interactive configuration process that:

- **Backend Selection**: Choose how the proxy connects to your APIs:
  - `apim` - Connect to an existing Azure API Management gateway
  - `null` - Use local mock/null servers for testing
  - `real` - Connect directly to real backend URLs

- **Configuration**: Set up proxy port, backend URLs, health probe paths, and request timeouts

- **Environment Generation**: Creates a `.azure/local-dev.env` file with all necessary environment variables

- **Instructions**: Provides step-by-step guidance for starting the proxy and any required test servers

This script is ideal for:
- Local development and debugging
- Testing proxy functionality without cloud deployment
- Rapid prototyping and experimentation
- Integration testing with mock services

After running the setup script, follow the provided instructions to start the proxy locally and begin testing.

## Deployment

SimpleL7Proxy can be deployed using the Azure Developer CLI (AZD) with an interactive setup script that guides you through the entire process:

```bash
# Navigate to the .azure directory and run the setup script
cd .azure
./setup.sh
```

The `setup.sh` script provides a deployment wizard that:

### Prerequisites Check
- Verifies Azure Developer CLI (AZD) installation
- Confirms Azure CLI availability
- Validates authentication requirements

### Deployment Scenarios
Choose from predefined deployment scenarios:
1. **Local proxy with public APIM** - Run proxy locally while connecting to Azure API Management
2. **ACA proxy with public APIM** (Recommended) - Deploy as Azure Container App with public APIM
3. **VNET proxy deployment** - Deploy within Virtual Network for enhanced security

### Environment Templates
Apply optimized configuration templates for your specific operational needs:
- [Standard Production](/.azure/env-templates/standard-production.env): Balanced for most production workloads
- [High Performance](/.azure/env-templates/high-performance.env): Optimized for maximum throughput and low latency
- [Cost Optimized](/.azure/env-templates/cost-optimized.env): Minimizes resource usage and cost
- [High Availability](/.azure/env-templates/high-availability.env): Maximized for resilience and uptime
- [Development](/.azure/env-templates/development.env): Configured for development and testing

### Configuration Management
- Interactive prompts for Azure subscription and resource configuration
- Backend URL setup with support for multiple endpoints
- Timeout and performance parameter configuration
- Automatic environment variable generation for AZD

### Next Steps After Setup
The script provides clear next steps based on your chosen scenario:
- **Cloud deployment**: Run `azd up` to provision and deploy everything
- **Local development**: Navigate to `src/SimpleL7Proxy` and run `dotnet run`

For detailed deployment instructions, see [Container Deployment](docs/CONTAINER_DEPLOYMENT.md).

