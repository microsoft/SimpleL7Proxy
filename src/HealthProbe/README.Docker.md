# HealthProbe Docker Build

This directory contains the Docker build configuration for the HealthProbe standalone health check service.

## Overview

The HealthProbe service runs a lightweight Kestrel web server on port 9000, providing health check endpoints for container orchestration platforms like Kubernetes and Azure Container Apps.

## Files

- **Dockerfile** - Multi-stage build configuration for creating the HealthProbe container image
- **.dockerignore** - Excludes unnecessary files from the build context
- **build.sh** - Bash script to build and optionally push the Docker image

## Building the Image

### Quick Build

From the `src/HealthProbe` directory:

```bash
chmod +x build.sh
./build.sh
```

This creates an image tagged as `healthprobe:latest`.

### Custom Tag

```bash
IMAGE_TAG=v2.2.8 ./build.sh
```

### Build for Registry

```bash
REGISTRY=myregistry.azurecr.io IMAGE_TAG=v2.2.8 ./build.sh
```

### Build and Push

```bash
REGISTRY=myregistry.azurecr.io IMAGE_TAG=v2.2.8 PUSH=true ./build.sh
```

### Manual Docker Build

From the `src` directory (parent of HealthProbe):

```bash
cd src
docker build -f HealthProbe/Dockerfile -t healthprobe:latest .
```

## Running the Container

### Basic Run

```bash
docker run -p 9000:9000 healthprobe:latest
```

### Run with Environment Variables

```bash
docker run -p 9000:9000 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Logging__LogLevel__Default=Debug \
  healthprobe:latest
```

### Run in Background

```bash
docker run -d -p 9000:9000 --name healthprobe healthprobe:latest
```

## Testing the Service

Once running, test the health endpoints:

```bash
# Health check
curl http://localhost:9000/health

# Readiness check
curl http://localhost:9000/readiness

# Startup check
curl http://localhost:9000/startup

# Liveness check
curl http://localhost:9000/liveness
```

## Health Check Endpoints

- **GET /health** - Overall health status (200 if healthy)
- **GET /readiness** - Indicates if the service is ready to receive traffic
- **GET /startup** - Startup probe for initial container health
- **GET /liveness** - Liveness probe to detect deadlocked processes

## Image Details

### Base Images

- **Build**: `mcr.microsoft.com/dotnet/sdk:9.0`
- **Runtime**: `mcr.microsoft.com/dotnet/aspnet:9.0`

### Exposed Ports

- **9000**: Health probe HTTP endpoints

### Security

- Runs as non-root user
- Only includes runtime dependencies (no SDK)
- Minimal attack surface

### Size Optimization

Multi-stage build ensures:
- Build dependencies stay in build stage
- Runtime image only contains necessary files
- Smaller final image size

## Azure Container Registry

### Login to ACR

```bash
az acr login --name myregistry
```

### Build and Push to ACR

```bash
REGISTRY=myregistry.azurecr.io IMAGE_TAG=v2.2.8 PUSH=true ./build.sh
```

### Or using Docker directly

```bash
docker build -f Dockerfile -t myregistry.azurecr.io/healthprobe:v2.2.8 ../
docker push myregistry.azurecr.io/healthprobe:v2.2.8
```

## Docker Compose

Example `docker-compose.yml`:

```yaml
version: '3.8'
services:
  healthprobe:
    build:
      context: ../
      dockerfile: HealthProbe/Dockerfile
    ports:
      - "9000:9000"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - Logging__LogLevel__Default=Information
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:9000/health"]
      interval: 30s
      timeout: 3s
      retries: 3
      start_period: 5s
```

## Troubleshooting

### View Logs

```bash
docker logs healthprobe
```

### Follow Logs

```bash
docker logs -f healthprobe
```

### Exec into Container

```bash
docker exec -it healthprobe /bin/bash
```

### Check Container Health

```bash
docker inspect --format='{{.State.Health.Status}}' healthprobe
```

### Common Issues

1. **Port already in use**: Change port mapping `-p 9001:9000`
2. **Permission denied**: Ensure build.sh is executable with `chmod +x build.sh`
3. **Build fails**: Run from correct directory (build context should be `src/`)

## Version

Current version: **2.2.8.d15** (as defined in `Constants.cs`)
